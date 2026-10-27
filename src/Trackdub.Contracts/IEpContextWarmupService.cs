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
    /// <summary>
    /// Loads the machine hardware identity asynchronously and publishes it to the
    /// EP-context load-path resolver, so pooled session creation can resolve cached
    /// artifacts without ever probing (or blocking) on the session-creation path.
    /// Host startup awaits this before the first session is created. Until it runs,
    /// EP-context lookups are honest cache misses and no artifact is stamped with
    /// the placeholder "unknown" identity.
    /// </summary>
    Task InitializeHardwareIdentityAsync(CancellationToken cancellationToken = default);

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
