using Trackdub.Application.Pipeline;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Subscribes engine auto-warming to model install notifications. Kept separate from the
/// coordinator so the coordinator stays a pure queue that tests can drive directly, and so the
/// subscription lifetime is explicit.
/// </summary>
public sealed class EngineAutoWarmInstaller : IDisposable
{
    private readonly IDisposable? subscription;
    private readonly IApplicationLogger? logger;

    public EngineAutoWarmInstaller(
        IModelDownloadOrchestrator downloadOrchestrator,
        IEngineAutoWarmQueue queue,
        IApplicationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(downloadOrchestrator);
        ArgumentNullException.ThrowIfNull(queue);
        this.logger = logger;

        // Only a transition INTO an installed/ready state should warm: a failed download, an
        // uninstall (-> Missing), or a repair still in progress must not build engines.
        subscription = downloadOrchestrator.StateChanges.Subscribe(
            new InstalledModelObserver(this, queue));
    }

    internal static bool IsInstalled(ModelCacheState state) =>
        state is ModelCacheState.Installed or ModelCacheState.Ready;

    private void OnStateChanged(ModelStateChange change, IEngineAutoWarmQueue queue)
    {
        if (!IsInstalled(change.NewState))
        {
            return;
        }

        try
        {
            queue.RequestWarmAfterInstall(change.ModelId);
        }
        catch (Exception ex)
        {
            // The queue is an optimization; an observer exception must never break the
            // download path or the observable stream.
            logger?.LogWarning($"Post-install engine warm could not be queued for '{change.ModelId}'.", ex);
        }
    }

    public void Dispose() => subscription?.Dispose();

    /// <summary>
    /// Minimal <see cref="IObserver{T}"/> adapter: this layer has no System.Reactive dependency,
    /// and the observable only ever emits values (completion carries no work).
    /// </summary>
    private sealed class InstalledModelObserver(EngineAutoWarmInstaller owner, IEngineAutoWarmQueue queue)
        : IObserver<ModelStateChange>
    {
        public void OnNext(ModelStateChange value) => owner.OnStateChanged(value, queue);

        public void OnError(Exception error) =>
            owner.logger?.LogWarning("Model state change stream faulted; post-install engine warming stops.", error);

        public void OnCompleted()
        {
        }
    }
}
