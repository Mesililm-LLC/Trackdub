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
    /// Marks the warm queue active for the host. The worker starts with queue construction, and
    /// requests self-activate if host startup did not run this service; activation records host
    /// startup and selects the Information-level activation log rather than gating processing.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        queue.Activate();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
