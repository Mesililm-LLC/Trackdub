using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Serving;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Serving;

namespace Trackdub.Inference.Onnx.Tests.Serving;

public sealed class ResidentInferenceHostTests : IDisposable
{
    private readonly string modelDirectory = Path.Join(Path.GetTempPath(), $"trackdub-serve-{Guid.NewGuid():N}");
    private readonly string modelPath;
    private readonly InferenceSessionPool pool = new(maxSessions: 4, enableMemoryAdmission: false);
    private int sessionsCreated;

    public ResidentInferenceHostTests()
    {
        Directory.CreateDirectory(modelDirectory);
        modelPath = Path.Join(modelDirectory, "identity.onnx");
        File.WriteAllBytes(modelPath, IdentityModel);
    }

    public void Dispose()
    {
        pool.Dispose();
        try
        {
            Directory.Delete(modelDirectory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    [Fact]
    public async Task LoadThenRun_ReturnsTheModelOutputOnCpu()
    {
        using ResidentInferenceHost host = CreateHost();

        ResidentModelInfo info = await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);
        ResidentRunResult result = await host.RunAsync("id", [Input(3f)], TestContext.Current.CancellationToken);

        Assert.Equal("cpu", info.SelectedProvider);
        Assert.Empty(info.FallbackChain);
        ServeTensorSpec input = Assert.Single(info.Inputs);
        Assert.Equal(("x", ServeTensorTypes.Float32), (input.Name, input.DType));
        Assert.Equal([1L], input.Shape);
        ServeTensor output = Assert.Single(result.Outputs);
        Assert.Equal("y", output.Name);
        Assert.Equal([3f], (float[])output.Data);
        Assert.Equal("cpu", result.SelectedProvider);
    }

    [Fact]
    public async Task RepeatedRuns_ReuseTheResidentSession()
    {
        using ResidentInferenceHost host = CreateHost();
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        for (int i = 0; i < 3; i++)
        {
            await host.RunAsync("id", [Input(i)], TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, sessionsCreated);
    }

    [Fact]
    public async Task Unload_EvictsTheSessionSoAReloadBuildsANewOne()
    {
        using ResidentInferenceHost host = CreateHost();
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        Assert.True(await host.UnloadAsync("id"));
        Assert.False(await host.UnloadAsync("id"));
        Assert.Empty(host.Describe());
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        Assert.Equal(2, sessionsCreated);
    }

    [Fact]
    public async Task Reload_UnderTheSameName_ReplacesTheModelAndEvictsTheOldSession()
    {
        string otherPath = Path.Join(modelDirectory, "identity-copy.onnx");
        File.Copy(modelPath, otherPath);
        using ResidentInferenceHost host = CreateHost();
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        ResidentModelInfo reloaded = await host.LoadAsync(
            new ResidentModelSpec("id", otherPath, ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);
        await host.LoadAsync(
            new ResidentModelSpec("id", otherPath, ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        Assert.Equal(otherPath, reloaded.ModelPath);
        Assert.Equal(otherPath, Assert.Single(host.Describe()).ModelPath);
        // The identical reload builds a new session too: the replaced one was evicted, not reused.
        Assert.Equal(3, sessionsCreated);
        ResidentRunResult result = await host.RunAsync("id", [Input(4f)], TestContext.Current.CancellationToken);
        Assert.Equal([4f], (float[])Assert.Single(result.Outputs).Data);
    }

    [Fact]
    public async Task Run_UnknownModel_ReportsUnknownModel()
    {
        using ResidentInferenceHost host = CreateHost();

        var ex = await Assert.ThrowsAsync<ResidentInferenceException>(
            () => host.RunAsync("missing", [Input(1f)], TestContext.Current.CancellationToken));

        Assert.Equal(ResidentInferenceErrorReasons.UnknownModel, ex.Reason);
    }

    [Fact]
    public async Task Load_MissingFile_ReportsModelNotFound()
    {
        using ResidentInferenceHost host = CreateHost();

        var ex = await Assert.ThrowsAsync<ResidentInferenceException>(() => host.LoadAsync(
            new ResidentModelSpec("id", Path.Join(modelDirectory, "absent.onnx")),
            TestContext.Current.CancellationToken));

        Assert.Equal(ResidentInferenceErrorReasons.ModelNotFound, ex.Reason);
    }

    public static TheoryData<ServeTensor[]> MismatchedInputs => new()
    {
        new[] { new ServeTensor("x", ServeTensorTypes.Int64, [1], new long[] { 1 }) },
        new[] { new ServeTensor("x", ServeTensorTypes.Float32, [2], new float[] { 1, 2 }) },
        new[] { new ServeTensor("x", ServeTensorTypes.Float32, [1], new float[] { 1, 2 }) },
        new[] { new ServeTensor("x", ServeTensorTypes.Float32, [1], new long[] { 1 }) },
        new[] { new ServeTensor("z", ServeTensorTypes.Float32, [1], new float[] { 1 }) },
        Array.Empty<ServeTensor>(),
    };

    [Fact]
    public void Validate_DynamicShapeTooLargeForOneArray_ReportsBadInputs()
    {
        using var session = new InferenceSession(DynamicIdentityModel);

        var ex = Assert.Throws<ResidentInferenceException>(() => ServeTensorConverter.Validate(
            session, [new ServeTensor("x", ServeTensorTypes.Float32, [long.MaxValue], new float[1])]));

        Assert.Equal(ResidentInferenceErrorReasons.BadInputs, ex.Reason);
        Assert.Contains("too large", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MismatchedInputs))]
    public async Task Run_MismatchedInputs_ReportsBadInputsWithoutPoisoning(ServeTensor[] inputs)
    {
        using ResidentInferenceHost host = CreateHost();
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<ResidentInferenceException>(
            () => host.RunAsync("id", inputs, TestContext.Current.CancellationToken));

        Assert.Equal(ResidentInferenceErrorReasons.BadInputs, ex.Reason);
        Assert.False(Assert.Single(host.Describe()).Poisoned);
    }

    [Fact]
    public async Task RequiredUnavailableProvider_ReportsProviderUnavailableWithoutFallback()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "CoreML is available on macOS.");
        using ResidentInferenceHost host = CreateHost();

        var ex = await Assert.ThrowsAsync<ResidentInferenceException>(
            () => host.LoadAsync(Spec(ExecutionProviderKind.CoreMl, require: true), TestContext.Current.CancellationToken));

        Assert.Equal(ResidentInferenceErrorReasons.ProviderUnavailable, ex.Reason);
        Assert.Empty(host.Describe());
    }

    [Fact]
    public async Task SoftUnavailableProvider_FallsBackAndRecordsTheChain()
    {
        Assert.SkipWhen(OperatingSystem.IsMacOS(), "CoreML is available on macOS.");
        using ResidentInferenceHost host = CreateHost();

        ResidentModelInfo info = await host.LoadAsync(Spec(ExecutionProviderKind.CoreMl), TestContext.Current.CancellationToken);
        ResidentRunResult result = await host.RunAsync("id", [Input(5f)], TestContext.Current.CancellationToken);

        Assert.Equal("coreml", info.RequestedProvider);
        Assert.Equal("cpu", info.SelectedProvider);
        Assert.Equal("coreml", Assert.Single(info.FallbackChain).Provider);
        Assert.Equal([5f], (float[])Assert.Single(result.Outputs).Data);
    }

    [Fact]
    public async Task AutoProvider_PicksTheBestAvailableInProbeOrder()
    {
        using ResidentInferenceHost host = CreateHost(
            available: [ExecutionProviderKind.Cpu, ExecutionProviderKind.Dnnl]);

        ResidentModelInfo info = await host.LoadAsync(
            new ResidentModelSpec("id", modelPath), TestContext.Current.CancellationToken);

        Assert.Equal("dnnl", info.RequestedProvider);
    }

    [Fact]
    public async Task FailedRun_PoisonsTheModel_AndTheNextRequestReopensIt()
    {
        using ResidentInferenceHost host = CreateHost();
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);
        int failures = 1;
        host.BeforeRun = _ =>
        {
            if (failures-- > 0)
            {
                throw new InvalidOperationException("device lost");
            }
        };

        var ex = await Assert.ThrowsAsync<ResidentInferenceException>(
            () => host.RunAsync("id", [Input(1f)], TestContext.Current.CancellationToken));
        Assert.Equal(ResidentInferenceErrorReasons.InferFailed, ex.Reason);
        Assert.True(Assert.Single(host.Describe()).Poisoned);

        ResidentRunResult result = await host.RunAsync("id", [Input(2f)], TestContext.Current.CancellationToken);

        Assert.Equal([2f], (float[])Assert.Single(result.Outputs).Data);
        Assert.False(Assert.Single(host.Describe()).Poisoned);
        Assert.Equal(2, sessionsCreated);
    }

    [Fact]
    public async Task Warm_WithoutInputs_RunsSynthesizedZeros()
    {
        using ResidentInferenceHost host = CreateHost();
        await host.LoadAsync(Spec(ExecutionProviderKind.Cpu), TestContext.Current.CancellationToken);

        ResidentRunResult? result = await host.WarmAsync("id", cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal([0f], (float[])Assert.Single(result.Outputs).Data);
    }

    [Fact]
    public void SynthesizeZeroInputs_TurnsDynamicDimensionsIntoOne()
    {
        IReadOnlyList<ServeTensor>? tensors = ServeTensorConverter.SynthesizeZeroInputs(
            [new ServeTensorSpec("ids", ServeTensorTypes.Int64, [-1, 3]), new ServeTensorSpec("mask", ServeTensorTypes.Bool, [2])]);

        Assert.NotNull(tensors);
        Assert.Equal([1L, 3L], tensors[0].Shape);
        Assert.Equal(new long[3], (long[])tensors[0].Data);
        Assert.Equal(new bool[2], (bool[])tensors[1].Data);
    }

    [Fact]
    public void SynthesizeZeroInputs_ShapeTooLargeForOneArrayReturnsNull() =>
        Assert.Null(ServeTensorConverter.SynthesizeZeroInputs(
            [new ServeTensorSpec("huge", ServeTensorTypes.Float32, [1L << 20, 1L << 20, 1L << 30])]));

    [Fact]
    public void SynthesizeZeroInputs_UnsupportedTypeReturnsNull() =>
        Assert.Null(ServeTensorConverter.SynthesizeZeroInputs([new ServeTensorSpec("text", "string", [1])]));

    public static TheoryData<string, Array> RoundTripTensors => new()
    {
        { ServeTensorTypes.Float32, new float[] { 1.5f, -2f } },
        { ServeTensorTypes.Float16, new Half[] { (Half)1.5f, (Half)(-2f) } },
        { ServeTensorTypes.Int64, new long[] { long.MaxValue, -1 } },
        { ServeTensorTypes.Int32, new int[] { 7, -7 } },
        { ServeTensorTypes.Int8, new sbyte[] { 1, -1 } },
        { ServeTensorTypes.UInt8, new byte[] { 0, 255 } },
        { ServeTensorTypes.Bool, new bool[] { true, false } },
    };

    [Theory]
    [MemberData(nameof(RoundTripTensors))]
    public void Converter_RoundTripsEverySupportedType(string dtype, Array data)
    {
        using OrtValue value = ServeTensorConverter.ToOrtValue(new ServeTensor("t", dtype, [2], data));

        ServeTensor roundTripped = ServeTensorConverter.FromOrtValue("t", value);

        Assert.Equal(dtype, roundTripped.DType);
        Assert.Equal([2L], roundTripped.Shape);
        Assert.Equal(data, roundTripped.Data);
    }

    private ResidentInferenceHost CreateHost(IReadOnlyList<ExecutionProviderKind>? available = null) =>
        new(
            available is null ? null : _ => Task.FromResult(available),
            pool,
            (path, options) =>
            {
                Interlocked.Increment(ref sessionsCreated);
                return new InferenceSession(path, options);
            });

    private ResidentModelSpec Spec(ExecutionProviderKind provider, bool require = false) =>
        new("id", modelPath, provider, require);

    private static ServeTensor Input(float value) =>
        new("x", ServeTensorTypes.Float32, [1], new[] { value });

    // IdentityModel with both dims symbolic (dim_param "N" instead of dim_value 1).
    private static readonly byte[] DynamicIdentityModel =
    [
        0x08, 0x07, 0x3A, 0x3C,
        0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79,
        0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x0B, 0x0A, 0x09, 0x08, 0x01, 0x12, 0x05, 0x0A, 0x03, 0x12, 0x01, 0x4E,
        0x62, 0x10, 0x0A, 0x01, 0x79, 0x12, 0x0B, 0x0A, 0x09, 0x08, 0x01, 0x12, 0x05, 0x0A, 0x03, 0x12, 0x01, 0x4E,
        0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ];

    // Identity graph x (float32 [1]) -> y (float32 [1]); same bytes as InferenceSessionPoolTests.
    private static readonly byte[] IdentityModel =
    [
        0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
        0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
        0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ];
}
