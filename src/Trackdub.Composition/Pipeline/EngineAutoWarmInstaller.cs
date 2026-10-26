using Microsoft.Extensions.Hosting;
using Trackdub.Application.Pipeline;
using Trackdub.Contracts.Licensing;
using Trackdub.Licensing;
using Trackdub.Composition.Tts;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Activates the post-install warm queue when the application host starts.
/// </summary>
public sealed class EngineAutoWarmInstaller : IHostedService
{
    private readonly IEngineAutoWarmQueue queue;
    private readonly ILicenseInitializer? licenseInitializer;
    private readonly KokoroVoiceCatalogHolder? voiceCatalogHolder;

    public EngineAutoWarmInstaller(
        IEngineAutoWarmQueue queue,
        ILicenseInitializer? licenseInitializer = null,
        KokoroVoiceCatalogHolder? voiceCatalogHolder = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.licenseInitializer = licenseInitializer;
        this.voiceCatalogHolder = voiceCatalogHolder;
    }

    /// <summary>
    /// Records host activation of the warm queue. This is not a processing gate: the coordinator
    /// starts its worker in its constructor and self-activates on the first request, so warming
    /// works whether or not a host ever starts this service. Starting it makes activation
    /// observable, which is how a misconfigured host becomes visible rather than silent.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (voiceCatalogHolder is not null)
        {
            await voiceCatalogHolder.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        if (licenseInitializer is not null)
        {
            await licenseInitializer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        // Call the contract directly for every implementation: a type-specific branch would leave
        // other IEngineAutoWarmQueue implementations with IsHostStarted stuck at false.
        queue.MarkHostStarted();
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
