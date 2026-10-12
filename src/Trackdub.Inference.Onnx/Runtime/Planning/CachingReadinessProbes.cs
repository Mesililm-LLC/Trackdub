using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

/// <summary>
/// Thread-safe memoizing base for execution-provider readiness probes. The first
/// <c>ProbeAsync</c> for a given <c>allowProviderDownloads</c> value is executed once and its
/// result is reused by every subsequent caller, so a single <c>providers list</c> invocation no
/// longer runs the same native/ORT readiness check twice (once via
/// <see cref="OnnxExecutionProviderDiscovery"/> and again via the *RuntimeReadinessService
/// wrappers, which share the same probe singleton).
/// </summary>
/// <remarks>
/// The cache keys on <c>allowProviderDownloads</c> so an <c>allowProviderDownloads:true</c> install
/// path is never served a value cached from an <c>allowProviderDownloads:false</c> probe. Concurrent
/// callers (BuildRemediationsAsync fans out via Task.WhenAll) share one in-flight probe rather than
/// racing. The cached value is always the actual first probe result: readiness is never fabricated.
/// A faulted or cancelled underlying probe is not cached permanently, so a later call re-probes.
/// <para>
/// Cancellation semantics: the shared underlying probe is started with
/// <see cref="CancellationToken.None"/> and runs to completion regardless of any single caller's
/// token, so a cancelled caller cannot transition the shared task to <see cref="TaskStatus.Canceled"/>
/// and poison concurrent callers. Each caller observes the shared result through its OWN token via
/// <c>WaitAsync</c>, so a caller whose token is cancelled sees an
/// <see cref="OperationCanceledException"/> without affecting other callers or cancelling the
/// underlying probe. The faulted/cancelled discard logic inspects the stored underlying task, not
/// the per-caller <c>WaitAsync</c> wrapper.
/// </para>
/// <para>
/// The memoized result lives for the lifetime of the singleton registration, so callers that
/// perform a state-changing operation (install/register/download) must call
/// <see cref="Invalidate"/> before re-probing to verify the new state; otherwise the re-probe
/// would observe the stale pre-change snapshot.
/// </para>
/// </remarks>
public abstract class CachingReadinessProbe<TReport> : IReadinessProbeCache
{
    private readonly Func<bool, CancellationToken, Task<TReport>> _probe;
    private readonly object _gate = new();
    private Task<TReport>? _cachedForDownloadsDisabled;
    private Task<TReport>? _cachedForDownloadsEnabled;

    protected CachingReadinessProbe(Func<bool, CancellationToken, Task<TReport>> probe) =>
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));

    /// <summary>
    /// Discards both cached readiness results so the next probe re-runs against current state.
    /// Used after a state-changing install/register so a verification re-probe does not observe the
    /// stale pre-change snapshot. This never fabricates a readiness value.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cachedForDownloadsDisabled = null;
            _cachedForDownloadsEnabled = null;
        }
    }

    protected Task<TReport> ProbeCachedAsync(bool allowProviderDownloads, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Task<TReport> started;
        lock (_gate)
        {
            Task<TReport>? existing = allowProviderDownloads
                ? _cachedForDownloadsEnabled
                : _cachedForDownloadsDisabled;

            // Only reuse a still-pending or successfully completed probe. A faulted or cancelled
            // underlying task is discarded so the next caller re-runs the actual probe.
            if (existing is not null &&
                existing.Status is not TaskStatus.Faulted and not TaskStatus.Canceled)
            {
                started = existing;
            }
            else
            {
                // Start the shared probe with CancellationToken.None so no single caller's
                // cancellation can poison the shared task for concurrent callers. The stored
                // task is this underlying task, which the discard check above inspects.
                started = _probe(allowProviderDownloads, CancellationToken.None);

                if (allowProviderDownloads)
                {
                    _cachedForDownloadsEnabled = started;
                }
                else
                {
                    _cachedForDownloadsDisabled = started;
                }
            }
        }

        // Observe the shared task through the caller's own token: a cancelled caller sees an
        // OperationCanceledException without cancelling the underlying probe or other callers.
        return started.WaitAsync(cancellationToken);
    }
}

/// <summary>Caching decorator for <see cref="ITensorRtRtxReadinessProbe"/>.</summary>
public sealed class CachingTensorRtRtxReadinessProbe(ITensorRtRtxReadinessProbe inner)
    : CachingReadinessProbe<TensorRtRtxReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        ITensorRtRtxReadinessProbe
{
    public Task<TensorRtRtxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IMigraphxReadinessProbe"/>.</summary>
public sealed class CachingMigraphxReadinessProbe(IMigraphxReadinessProbe inner)
    : CachingReadinessProbe<MigraphxReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IMigraphxReadinessProbe
{
    public Task<MigraphxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        // The cache base hard-codes the underlying probe to run with CancellationToken.None,
        // so a percent sink attached to a cache-miss call would not see the in-flight events
        // from concurrent callers. Bypass the cache when a progress sink is requested; the
        // caller's token still controls cancellation, and the next status-only call repopulates
        // the cache as usual.
        if (progress is not null)
        {
            return inner.ProbeAsync(allowProviderDownloads, cancellationToken, progress);
        }

        return ProbeCachedAsync(allowProviderDownloads, cancellationToken);
    }
}

/// <summary>Caching decorator for <see cref="IDnnlReadinessProbe"/>.</summary>
public sealed class CachingDnnlReadinessProbe(IDnnlReadinessProbe inner)
    : CachingReadinessProbe<DnnlReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IDnnlReadinessProbe
{
    public Task<DnnlReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IOpenVinoCatalogReadinessProbe"/>.</summary>
public sealed class CachingOpenVinoCatalogReadinessProbe(IOpenVinoCatalogReadinessProbe inner)
    : CachingReadinessProbe<WinMlCatalogReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IOpenVinoCatalogReadinessProbe
{
    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IQnnCatalogReadinessProbe"/>.</summary>
public sealed class CachingQnnCatalogReadinessProbe(IQnnCatalogReadinessProbe inner)
    : CachingReadinessProbe<WinMlCatalogReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IQnnCatalogReadinessProbe
{
    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}

/// <summary>Caching decorator for <see cref="IVitisAiCatalogReadinessProbe"/>.</summary>
public sealed class CachingVitisAiCatalogReadinessProbe(IVitisAiCatalogReadinessProbe inner)
    : CachingReadinessProbe<WinMlCatalogReadinessReport>(
        (allowProviderDownloads, cancellationToken) =>
            (inner ?? throw new ArgumentNullException(nameof(inner)))
                .ProbeAsync(allowProviderDownloads, cancellationToken)),
        IVitisAiCatalogReadinessProbe
{
    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default) =>
        ProbeCachedAsync(allowProviderDownloads, cancellationToken);
}
