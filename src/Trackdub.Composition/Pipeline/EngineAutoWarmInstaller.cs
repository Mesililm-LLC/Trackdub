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
    /// Activates the warm queue. Hosting is what makes post-install warming live in production, so
    /// this is the activation point rather than a no-op: a host that never starts this service
    /// leaves the queue inactive, which is observable instead of silently dropping every request.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        queue.Activate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
