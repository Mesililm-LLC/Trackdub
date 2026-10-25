using Trackdub.Domain;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Requests an engine warm after a model install. A newly installed model has no TensorRT-RTX
/// EP-context artifact, so its first session builds engines from source (tens of seconds for a
/// multi-GB graph) and every later launch repeats that build until an artifact exists. Warming
/// right after install moves that cost off the first real run.
/// </summary>
/// <remarks>
/// Optimization only: implementations must never throw for a warm failure and must never block
/// the caller. Cold-start correctness never depends on a warm having happened.
/// </remarks>
public interface IEngineAutoWarmQueue
{
    /// <summary>
    /// Queues a warm for the enabled pipeline stages after <paramref name="modelId"/> finished
    /// installing or was already present. Duplicate requests for the same model coalesce.
    /// </summary>
    /// <param name="modelId">Manifest model id that became available.</param>
    /// <param name="cancellationToken">Cancels the queueing, not an already-started warm.</param>
    void RequestWarmAfterInstall(string modelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the queue to drain and in-flight warms to finish. Shutdown and test seam.
    /// Callers must not await this while holding a lock a warm needs.
    /// </summary>
    Task DrainAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Activates the queue for the hosting process and returns whether this call performed the
    /// activation (false when it was already active). A host calls this at startup so that a
    /// misconfigured host — one that never activates — is detectable rather than silently
    /// accepting requests that no worker will ever process.
    /// </summary>
    bool Activate();

    /// <summary>Whether <see cref="Activate"/> has run for this process.</summary>
    bool IsActive { get; }
}
