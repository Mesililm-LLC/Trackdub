using System.Diagnostics.Tracing;
using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Scenarios;

/// <summary>
/// Challenger 2 empirical stress test harness for Milestone 3:
/// - Offline isolation: zero network, zero live model downloads, zero GPU checks
/// - 10-iteration multi-run stability and 5-stage lifecycle / memory telemetry
/// - CLI DevHost argument permutations (--mock, --dry-run, --format, --providers, --baseline)
/// - Structured JSON output fidelity and mathematical consistency
/// </summary>
[Trait("Category", "Slow")]
public sealed class Challenger2StressTests : IDisposable
{
    private readonly MockDubbingBenchmarkHarness _harness = new();
    private readonly string _tempOutputDir;

    public Challenger2StressTests()
    {
        _tempOutputDir = Path.Join(Path.GetTempPath(), $"trackdub_ch2_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_tempOutputDir))
        {
            try { Directory.Delete(_tempOutputDir, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
        }
    }

    [Fact]
    public async Task Stress_OfflineIsolation_NoNetworkCallsDuringMockExecution()
    {
        // Setup listener for network events from System.Net
        int networkEventsCount = 0;
        using var networkListener = new TestNetworkEventListener(() => Interlocked.Increment(ref networkEventsCount));

        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = Path.Join(_tempOutputDir, "offline_network"),
            Mode = "fresh-process",
            RunCount = 2,
            Mock = true,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Equal(0, networkEventsCount);
    }

    [Fact]
    public async Task Stress_MultiRun10Iterations_PercentileStabilityAndAllFiveStagesCaptured()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = Path.Join(_tempOutputDir, "runs10_stability"),
            Mode = "fresh-process",
            RunCount = 10,
            Mock = true,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);

        // 1. Verify SampleCount and Monotonic Percentiles
        Assert.Equal(10.0, report.TimingsMilliseconds["pipeline:sampleCount"]);
        double min = report.TimingsMilliseconds["pipeline:min"]!.Value;
        double mean = report.TimingsMilliseconds["pipeline:mean"]!.Value;
        double p50 = report.TimingsMilliseconds["pipeline:p50"]!.Value;
        double p90 = report.TimingsMilliseconds["pipeline:p90"]!.Value;
        double p99 = report.TimingsMilliseconds["pipeline:p99"]!.Value;
        double max = report.TimingsMilliseconds["pipeline:max"]!.Value;

        Assert.True(min > 0, $"Min ({min}) > 0");
        Assert.True(min <= mean && mean <= max, $"Min ({min}) <= Mean ({mean}) <= Max ({max})");
        Assert.True(min <= p50, $"Min ({min}) <= P50 ({p50})");
        Assert.True(p50 <= p90, $"P50 ({p50}) <= P90 ({p90})");
        Assert.True(p90 <= p99, $"P90 ({p90}) <= P99 ({p99})");
        Assert.True(p99 <= max, $"P99 ({p99}) <= Max ({max})");

        // 2. Verify all 5 canonical stages exist in Stages list
        string[] canonicalStages = ["audio-prep", "separation", "transcription", "alignment", "dubbing"];
        foreach (BenchmarkEvidenceStage? stage in canonicalStages.Select(stageName =>
            report.Stages.FirstOrDefault(s => s.Name.Equals(stageName, StringComparison.OrdinalIgnoreCase))))
        {
            Assert.NotNull(stage);
            Assert.Equal(BenchmarkEvidenceStatus.Completed, stage.Status);
            Assert.NotNull(stage.ActualModel);
            Assert.NotNull(stage.ActualProvider);
        }

        // 3. Verify typed memory telemetry across canonical stages
        Assert.NotNull(report.ProcessMemory);
        Assert.True(report.ProcessMemory.PeakWorkingSetBytes > 0, "peak working set must be sampled > 0");
        Assert.NotNull(report.ProcessMemory.ManagedAllocatedBytes);

        foreach (string stageName in canonicalStages)
        {
            BenchmarkStageResourceTelemetry[] samples = report.ResourceTelemetry
                .Where(sample => sample.Stage.Equals(stageName, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert.NotEmpty(samples);
            Assert.All(samples, sample =>
            {
                foreach (string metric in new[] { "workingSetBytes", "managedAllocatedBytes" })
                {
                    var check = Assert.Single(sample.Validation.Checks, check => check.Metric == metric);
                    Assert.NotNull(check.ObservedValue);
                    Assert.True(check.ObservedValue >= 0, $"{stageName} {metric} >= 0");
                }
            });
            BenchmarkStageGarbageCollectionTelemetry gc = Assert.Single(
                report.StageGarbageCollection,
                entry => entry.Stage.Equals(stageName, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(gc.Gen0Collections);
            Assert.True(gc.Gen0Collections >= 0, $"{stageName} gen0 >= 0");
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    [InlineData("both")]
    public async Task Stress_CliMatrix_ArgumentPermutationsAndFormatValidation(string format)
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        string outDir = Path.Join(_tempOutputDir, $"format_{format}");
        Directory.CreateDirectory(outDir);

        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", outDir, "--mock", "--providers", "cpu,directml,tensorrt", "--runs", "3", "--format", format],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string stdout = output.ToString();

        if (format is "console" or "both")
        {
            Assert.Contains("Execution Provider Matrix: full-pipeline (Baseline: cpu)", stdout);
            Assert.Contains("cpu:", stdout);
            Assert.Contains("directml:", stdout);
            Assert.Contains("tensorrt:", stdout);
        }

        if (format is "json" or "both")
        {
            string jsonPath = Path.Join(outDir, "execution-provider-matrix.json");
            Assert.True(File.Exists(jsonPath), $"Expected matrix JSON file at: {jsonPath}");

            string jsonContent = await File.ReadAllTextAsync(jsonPath);
            var report = JsonSerializer.Deserialize<ExecutionProviderMatrixReport>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            Assert.NotNull(report);
            Assert.Equal("full-pipeline", report.Scenario);
            Assert.Equal("cpu", report.BaselineProvider);
            Assert.Equal(3, report.Comparisons.Count);

            // Baseline assertions
            var baseline = report.Comparisons.First(c => c.Provider.Equals("cpu", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1.0, baseline.SpeedupFactor);
            Assert.Equal(0.0, baseline.LatencyDeltaMilliseconds);
            Assert.Equal(1.0, baseline.ThroughputRatio);
            Assert.Equal(0, baseline.PeakWorkingSetDeltaBytes);
            Assert.Equal(0, baseline.ManagedAllocatedDeltaBytes);

            // A mock run reports each provider's simulated latency budget on its own comparison
            // row, so the simulated contract is assertable from the report itself: DirectML is
            // budgeted at half the CPU baseline's simulated latency and TensorRT at a quarter,
            // i.e. the 2x and 4x speedups the simulation demonstrates. The measured ratios are
            // deliberately not asserted — every provider's run also pays a fixed cost (host setup,
            // resource telemetry sampling, the simulated allocations) of the same order as the
            // simulated gap, so measured TensorRT routinely exceeds measured DirectML. What the
            // budget does guarantee is a floor: a completed mock pipeline cannot measure below the
            // latency it was told to wait.
            var dml = report.Comparisons.First(c => c.Provider.Equals("directml", StringComparison.OrdinalIgnoreCase));
            var trt = report.Comparisons.First(c => c.Provider.Equals("tensorrt", StringComparison.OrdinalIgnoreCase));
            double baselineBudget = SimulatedBudget(baseline);
            double dmlBudget = SimulatedBudget(dml);
            double trtBudget = SimulatedBudget(trt);

            Assert.Equal(1000.0, baselineBudget);
            Assert.Equal(500.0, dmlBudget);
            Assert.Equal(250.0, trtBudget);
            Assert.Equal(2.0, baselineBudget / dmlBudget);
            Assert.Equal(4.0, baselineBudget / trtBudget);
            Assert.True(
                dml.P50Milliseconds >= dmlBudget - 1.0,
                $"DirectML P50 {dml.P50Milliseconds} contains its simulated latency budget of {dmlBudget} ms");
            Assert.True(
                trt.P50Milliseconds >= trtBudget - 1.0,
                $"TRT P50 {trt.P50Milliseconds} contains its simulated latency budget of {trtBudget} ms");
        }
    }

    [Fact]
    public async Task Stress_CliMatrix_DryRun_ExecutesWithMinimalLatency()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        string outDir = Path.Join(_tempOutputDir, "dry_run_test");

        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", outDir, "--dry-run", "--providers", "cpu,directml"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string jsonPath = Path.Join(outDir, "execution-provider-matrix.json");
        Assert.True(File.Exists(jsonPath));

        string jsonContent = await File.ReadAllTextAsync(jsonPath);
        var report = JsonSerializer.Deserialize<ExecutionProviderMatrixReport>(jsonContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(report);
        Assert.Equal(2, report.Comparisons.Count);
        // A dry run waits no simulated delay, so it must not claim a simulated latency budget.
        Assert.All(
            report.Comparisons,
            comparison => Assert.Null(comparison.SimulatedLatencyBudgetMilliseconds));
    }

    [Fact]
    public async Task Stress_CliMatrix_CustomBaselineProvider_ComputesRelativeMetricsAccurately()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        string outDir = Path.Join(_tempOutputDir, "custom_baseline");

        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", outDir, "--mock", "--baseline", "directml", "--providers", "cpu,directml,tensorrt"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string jsonPath = Path.Join(outDir, "execution-provider-matrix.json");
        Assert.True(File.Exists(jsonPath));

        string jsonContent = await File.ReadAllTextAsync(jsonPath);
        var report = JsonSerializer.Deserialize<ExecutionProviderMatrixReport>(jsonContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(report);
        Assert.Equal("directml", report.BaselineProvider);

        var baseline = report.Comparisons.First(c => c.Provider.Equals("directml", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1.0, baseline.SpeedupFactor);
        Assert.Equal(0.0, baseline.LatencyDeltaMilliseconds);

        // Against a DirectML baseline the mock's contract is that the CPU stages take twice as long.
        // Both budgets come from the report, and the measured ratio is not asserted for the reasons
        // given in the format-permutation test.
        var cpu = report.Comparisons.First(c => c.Provider.Equals("cpu", StringComparison.OrdinalIgnoreCase));
        double directmlBudget = SimulatedBudget(baseline);
        double cpuBudget = SimulatedBudget(cpu);

        Assert.Equal(500.0, directmlBudget);
        Assert.Equal(1000.0, cpuBudget);
        Assert.Equal(0.5, directmlBudget / cpuBudget);
        Assert.True(
            cpu.P50Milliseconds >= cpuBudget - 1.0,
            $"CPU P50 {cpu.P50Milliseconds} contains its simulated latency budget of {cpuBudget} ms");
    }

    /// <summary>
    /// A comparison row's simulated latency budget, which a mock run must report so the simulated
    /// contract can be asserted from the report instead of from the runner's helpers.
    /// </summary>
    private static double SimulatedBudget(ProviderComparisonMetrics comparison) =>
        comparison.SimulatedLatencyBudgetMilliseconds
            ?? throw new InvalidOperationException(
                $"Provider '{comparison.Provider}' has no simulated latency budget; the mock run did not report one.");

    private sealed class TestNetworkEventListener(Action onNetworkEvent) : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase))
            {
                EnableEvents(eventSource, EventLevel.LogAlways);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventArgs)
        {
            onNetworkEvent();
        }
    }
}
