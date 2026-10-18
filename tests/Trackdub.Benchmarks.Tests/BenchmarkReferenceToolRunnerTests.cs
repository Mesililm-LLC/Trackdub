using Trackdub.Benchmarks.ReferenceTool;
using Trackdub.Domain.Benchmarking;
using Xunit;

namespace Trackdub.Benchmarks.Tests;

public sealed class BenchmarkReferenceToolRunnerTests
{
    private static BenchmarkReferenceToolRunner.Request RequestFor(
        BenchmarkReferenceToolKind kind,
        string? explicitPath = null,
        bool deviceBound = false) =>
        new(
            kind,
            explicitPath,
            Required: false,
            ModelPath: "model.onnx",
            GraphRole: "primary",
            RunCount: 10,
            InputSeed: 42,
            DeviceBoundIo: deviceBound,
            EvidenceDirectory: Path.GetTempPath());

    [Fact]
    public async Task RunAsync_MissingExplicitPath_ReturnsNotAvailable()
    {
        string missing = Path.Join(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.exe");

        BenchmarkReferenceToolResult result = await BenchmarkReferenceToolRunner.RunAsync(
            RequestFor(BenchmarkReferenceToolKind.OrtPerfTest, explicitPath: missing),
            CancellationToken.None);

        Assert.Equal(BenchmarkReferenceStatus.NotAvailable, result.Status);
        Assert.Equal("primary", result.GraphRole);
        Assert.Contains("not found", result.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_WrongExecutable_ReturnsUnsupportedVersionWithHelpIdentity()
    {
        // The test host's own process (dotnet) is guaranteed present; its --help output
        // cannot identify itself as the NVIDIA tool, exercising the identity gate.
        string hostExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("No process path available.");

        BenchmarkReferenceToolResult result = await BenchmarkReferenceToolRunner.RunAsync(
            RequestFor(BenchmarkReferenceToolKind.OrtPerfTest, explicitPath: hostExecutable),
            CancellationToken.None);

        Assert.Equal(BenchmarkReferenceStatus.UnsupportedVersion, result.Status);
        Assert.Equal(hostExecutable, result.ExecutablePath);
        Assert.StartsWith("sha256:", result.HelpIdentity);
        Assert.NotNull(result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_Cancellation_Propagates()
    {
        string hostExecutable = Environment.ProcessPath
            ?? throw new InvalidOperationException("No process path available.");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => BenchmarkReferenceToolRunner.RunAsync(
                RequestFor(BenchmarkReferenceToolKind.OrtPerfTest, explicitPath: hostExecutable),
                cancelled.Token));
    }

    [Fact]
    public void OrtPerfTest_RequiredFlags_UseSingleDashHelpTokens()
    {
        var recipe = BenchmarkReferenceToolRunner.RecipeFor(BenchmarkReferenceToolKind.OrtPerfTest);

        Assert.Equal(["-e", "-m", "-r", "-s", "-I", "-S"], recipe.RequiredFlags(RequestFor(BenchmarkReferenceToolKind.OrtPerfTest)));
        Assert.Contains("-g", recipe.RequiredFlags(RequestFor(BenchmarkReferenceToolKind.OrtPerfTest, deviceBound: true)));
    }

    [Theory]
    [InlineData("  -e: [cpu|cuda] provider", "-e", true)]
    [InlineData("  -e, --execution_provider: provider", "-e", true)]
    [InlineData("  --execution_provider: provider", "-e", false)]
    [InlineData("  --e: provider", "-e", false)]
    [InlineData("  -execution_provider: provider", "-e", false)]
    public void HelpAdvertisesFlag_MatchesCompleteOptionToken(string help, string flag, bool expected) =>
        Assert.Equal(expected, BenchmarkReferenceToolRunner.HelpAdvertisesFlag(help, flag));

    [Fact]
    public void OrtPerfTest_Arguments_SelectNvTensorRtRtxProviderAndModel()
    {
        var recipe = BenchmarkReferenceToolRunner.RecipeFor(BenchmarkReferenceToolKind.OrtPerfTest);
        string[] args = recipe.BuildArguments(
            RequestFor(BenchmarkReferenceToolKind.OrtPerfTest, deviceBound: true), string.Empty);

        Assert.Equal("-e", args[0]);
        Assert.Equal("nvtensorrtrtx", args[1]);
        Assert.Contains("-r", args);
        Assert.Contains("10", args);
        Assert.Contains("-g", args);
        Assert.Equal("model.onnx", args[^1]);
    }

    [Fact]
    public void TensorRtRtx_Arguments_AlwaysIncludeDocVerifiedFlags()
    {
        var recipe = BenchmarkReferenceToolRunner.RecipeFor(BenchmarkReferenceToolKind.TensorRtRtx);
        string[] args = recipe.BuildArguments(
            RequestFor(BenchmarkReferenceToolKind.TensorRtRtx), "tensorrt_rtx --onnx --iterations --warmup");

        Assert.Contains("--onnx=model.onnx", args);
        Assert.Contains("--iterations=10", args);
        Assert.Contains("--warmup=200", args);
        // trtexec-style tuning flags must be skipped when --help does not advertise them.
        Assert.DoesNotContain("--useCudaGraph", args);
        Assert.Contains("--useCudaGraph", recipe.SkippedOptionalFlags(
            RequestFor(BenchmarkReferenceToolKind.TensorRtRtx), "tensorrt_rtx --onnx"));
    }

    [Fact]
    public void TensorRtRtx_Arguments_PassOptionalFlagsOnlyWhenAdvertised()
    {
        var recipe = BenchmarkReferenceToolRunner.RecipeFor(BenchmarkReferenceToolKind.TensorRtRtx);
        string helpText = "tensorrt_rtx --onnx --iterations --warmup --useCudaGraph --useSpinWait";

        string[] args = recipe.BuildArguments(
            RequestFor(BenchmarkReferenceToolKind.TensorRtRtx, deviceBound: true), helpText);

        Assert.Contains("--useCudaGraph", args);
        Assert.Contains("--useSpinWait", args);
        Assert.Empty(recipe.SkippedOptionalFlags(
            RequestFor(BenchmarkReferenceToolKind.TensorRtRtx, deviceBound: true), helpText));
    }

    [Fact]
    public void ParseMetrics_OrtPerfTest_ExtractsLatencyAndPercentiles()
    {
        const string stdout = """
            Session creation time cost: 0.123 s
            Total inference time cost: 0.456 s
            Total inference requests: 10
            Avg Latency: 4.56 ms
            Percentile Latency: {P50: 4.50 ms, P90: 5.00 ms, P95: 5.20 ms, P99: 6.00 ms}
            """;

        var metrics = BenchmarkReferenceToolRunner.ParseMetrics(
            BenchmarkReferenceToolKind.OrtPerfTest, stdout);

        var average = Assert.Single(metrics, m => m.Name == "avg-latency");
        Assert.Equal("ms", average.Unit);
        Assert.InRange(average.Value, 4.56 - 1e-9, 4.56 + 1e-9);
        var p50 = Assert.Single(metrics, m => m.Name == "latency-p50");
        Assert.InRange(p50.Value, 4.50 - 1e-9, 4.50 + 1e-9);
        var p99 = Assert.Single(metrics, m => m.Name == "latency-p99");
        Assert.InRange(p99.Value, 6.00 - 1e-9, 6.00 + 1e-9);
        Assert.Contains(metrics, m => m.Name == "session-creation" && m.TimingDomain == "session-init");
        Assert.All(metrics, m => Assert.False(string.IsNullOrWhiteSpace(m.TimingDomain)));
    }

    [Fact]
    public void ParseMetrics_TensorRtRtx_ExtractsGpuComputeTime()
    {
        const string stdout = """
            [I] GPU Compute Time: min = 1.23 ms, max = 2.34 ms, mean = 1.50 ms, median = 1.49 ms, percentile(90%) = 1.60 ms
            [I] Throughput: 123.45 qps
            """;

        var metrics = BenchmarkReferenceToolRunner.ParseMetrics(
            BenchmarkReferenceToolKind.TensorRtRtx, stdout);

        var mean = Assert.Single(metrics, m => m.Name == "gpu-compute-mean");
        Assert.Equal("gpu-compute", mean.TimingDomain);
        Assert.InRange(mean.Value, 1.50 - 1e-9, 1.50 + 1e-9);
        var minimum = Assert.Single(metrics, m => m.Name == "gpu-compute-min");
        Assert.InRange(minimum.Value, 1.23 - 1e-9, 1.23 + 1e-9);
        var throughput = Assert.Single(metrics, m => m.Name == "throughput");
        Assert.Equal("qps", throughput.Unit);
        Assert.InRange(throughput.Value, 123.45 - 1e-9, 123.45 + 1e-9);
    }

    [Fact]
    public void ParseMetrics_UnrecognizedOutput_ReturnsEmpty()
    {
        var metrics = BenchmarkReferenceToolRunner.ParseMetrics(
            BenchmarkReferenceToolKind.OrtPerfTest, "completely unrelated output");

        Assert.Empty(metrics);
    }
}
