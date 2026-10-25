using Microsoft.Extensions.Hosting;
using Trackdub.Application.Pipeline;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Activates the post-install warm queue when the application host starts.
/// </summary>
public sealed class EngineAutoWarmInstaller : IHostedService
{
    private readonly IEngineAutoWarmQueue queue;

    public EngineAutoWarmInstaller(IEngineAutoWarmQueue queue) =>
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));

    /// <summary>
    /// Records host activation of the warm queue. This is not a processing gate: the coordinator
    /// starts its worker in its constructor and self-activates on the first request, so warming
    /// works whether or not a host ever starts this service. Starting it makes activation
    /// observable, which is how a misconfigured host becomes visible rather than silent.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (queue is EngineAutoWarmCoordinator coordinator)
        {
            coordinator.MarkHostStarted();
        }
        else
        {
            queue.Activate();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
