using Trackdub.Domain;

namespace Trackdub.Contracts;

public sealed record EpContextWarmItem(
    string SourceModelPath,
    string Status,
    string? EpContextPath,
    double CompileMilliseconds,
    double WarmLoadMilliseconds,
    string? Detail);

public sealed record EpContextWarmReport(
    string EngineCacheDirectory,
    string EnvironmentFingerprint,
    int CompiledCount,
    int ReusedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<EpContextWarmItem> Items);

/// <summary>
/// Ahead-of-time EP-context precompile plus residual runtime-cache warmup.
/// Run after install, app update, GPU swap, driver change, or TRT RTX EP bump.
/// </summary>
public interface IEpContextWarmupService
{
    /// <param name="provider">
    /// <see cref="ExecutionProviderKind.TensorRTRtx"/> (default) keeps the EP-context AOT +
    /// runtime-cache warm behavior. <see cref="ExecutionProviderKind.TensorRt"/> warms the
    /// classic TensorRT engine cache by creating real sessions on the original graphs.
    /// </param>
    Task<EpContextWarmReport> WarmAsync(
        IReadOnlyList<string>? sourceModelPaths = null,
        IProgress<string>? progress = null,
        ExecutionProviderKind provider = ExecutionProviderKind.TensorRTRtx,
        CancellationToken cancellationToken = default);
}
