namespace Trackdub.Domain.Benchmarking;

public enum BenchmarkInputMode
{
    Synthetic,
    Zeros
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
    bool EnableCudaGraph = false,
    BenchmarkArtifactMode ArtifactMode = BenchmarkArtifactMode.Auto,
    BenchmarkRuntimeCacheMode CacheMode = BenchmarkRuntimeCacheMode.Reuse,
    string? RuntimeCacheDirectory = null);

/// <summary>Explicit budgets; a zero measurement duration preserves exact requested counts.</summary>
public sealed record BenchmarkExecutionSettings(
    int MinimumWarmupRuns = 1,
    int MinimumWarmupMilliseconds = 200,
    int MinimumMeasurementMilliseconds = 0,
    BenchmarkInputMode InputMode = BenchmarkInputMode.Synthetic,
    int InputSeed = 42,
    BenchmarkIoMode IoMode = BenchmarkIoMode.Managed,
    BenchmarkTensorRtRtxSettings? TensorRtRtx = null)
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
            || !Enum.IsDefined(EffectiveTensorRtRtx.CacheMode))
        {
            throw new ArgumentException("Unrecognized benchmark execution setting.");
        }

        if (EffectiveTensorRtRtx.EnableCudaGraph && IoMode != BenchmarkIoMode.DeviceBound)
        {
            throw new ArgumentException("CUDA graphs require persistent device-bound I/O.");
        }
    }
}
