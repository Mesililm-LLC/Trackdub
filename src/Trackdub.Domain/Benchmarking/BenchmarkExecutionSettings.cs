namespace Trackdub.Domain.Benchmarking;

public enum BenchmarkInputMode
{
    Synthetic,
    Zeros,
    /// <summary>
    /// Resolve runtime shapes from the registered production trt-rtx optimization profile
    /// (min/opt/max) for the model family; fails when no profile is registered.
    /// </summary>
    Profile
}

public enum BenchmarkIoMode
{
    Managed,
    DeviceBound
}

public enum BenchmarkArtifactMode
{
    Auto,
    Source,
    Require
}

public enum BenchmarkRuntimeCacheMode
{
    Reuse,
    Isolated
}

public enum BenchmarkCudaGraphMode
{
    /// <summary>Enable graphs when the run is device-bound on a single-graph trt-rtx session; otherwise off.</summary>
    Auto,
    Off,
    On,
    Compare
}

public enum BenchmarkThroughputUnit
{
    PassesPerSecond,
    ItemsPerSecond,
    AudioSecondsPerSecond
}

public sealed record BenchmarkTensorRtRtxSettings(
    BenchmarkCudaGraphMode CudaGraphMode = BenchmarkCudaGraphMode.Auto,
    BenchmarkArtifactMode ArtifactMode = BenchmarkArtifactMode.Auto,
    BenchmarkRuntimeCacheMode CacheMode = BenchmarkRuntimeCacheMode.Reuse,
    string? RuntimeCacheDirectory = null)
{
    /// <summary>Any lane that explicitly requests a CUDA-graph session (On or the on-half of Compare).</summary>
    public bool EnableCudaGraph => CudaGraphMode is BenchmarkCudaGraphMode.On or BenchmarkCudaGraphMode.Compare;

    /// <summary>
    /// Resolve the effective enable_cuda_graph flag. Auto enables graphs only when the run
    /// satisfies the documented precondition (persistent device-bound I/O on a single-graph
    /// trt-rtx session); explicit modes keep their requested value.
    /// </summary>
    public bool ResolveCudaGraphEnable(bool deviceBoundEligible) => CudaGraphMode switch
    {
        BenchmarkCudaGraphMode.On or BenchmarkCudaGraphMode.Compare => true,
        BenchmarkCudaGraphMode.Auto => deviceBoundEligible,
        _ => false
    };
}

/// <summary>Explicit budgets; a zero measurement duration preserves exact requested counts.</summary>
public sealed record BenchmarkExecutionSettings(
    int MinimumWarmupRuns = 1,
    int MinimumWarmupMilliseconds = 200,
    int MinimumMeasurementMilliseconds = 0,
    BenchmarkInputMode InputMode = BenchmarkInputMode.Synthetic,
    int InputSeed = 42,
    BenchmarkIoMode IoMode = BenchmarkIoMode.Managed,
    BenchmarkTensorRtRtxSettings? TensorRtRtx = null,
    IReadOnlyDictionary<string, int[]>? InputShapeOverrides = null)
{
    public static BenchmarkExecutionSettings Default { get; } = new();

    public BenchmarkTensorRtRtxSettings EffectiveTensorRtRtx =>
        TensorRtRtx ?? new BenchmarkTensorRtRtxSettings();

    public void Validate(int requestedSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requestedSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumWarmupRuns, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumWarmupMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(MinimumMeasurementMilliseconds);
        if (!Enum.IsDefined(InputMode) || !Enum.IsDefined(IoMode)
            || !Enum.IsDefined(EffectiveTensorRtRtx.ArtifactMode)
            || !Enum.IsDefined(EffectiveTensorRtRtx.CacheMode)
            || !Enum.IsDefined(EffectiveTensorRtRtx.CudaGraphMode))
        {
            throw new ArgumentException("Unrecognized benchmark execution setting.");
        }

        if (InputShapeOverrides is not null)
        {
            foreach ((string name, int[] dims) in InputShapeOverrides)
            {
                if (string.IsNullOrWhiteSpace(name) || dims.Length == 0 || dims.Any(d => d < 1))
                {
                    throw new ArgumentException(
                        $"Input shape override '{name}' must name a non-empty input and use dimensions >= 1.");
                }
            }
        }

        if (EffectiveTensorRtRtx.EnableCudaGraph && IoMode != BenchmarkIoMode.DeviceBound)
        {
            throw new ArgumentException(
                "CUDA graphs require persistent device-bound I/O; --io-mode device-bound is required for on/compare.");
        }
    }
}
