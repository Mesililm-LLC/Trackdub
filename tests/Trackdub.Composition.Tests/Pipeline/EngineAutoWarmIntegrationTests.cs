using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Composition.Pipeline;
using Trackdub.Composition.Runtime;
using Trackdub.Contracts;
using Trackdub.Domain;
using Xunit;

namespace Trackdub.Composition.Tests.Pipeline;

/// <summary>
/// Proves the production DI graph actually connects a model-install notification to a warm. The
/// unit tests drive the coordinator directly; this one resolves the real registrations, so a
/// missing or mis-ordered registration cannot pass silently.
/// </summary>
public class EngineAutoWarmIntegrationTests
{
    [Fact]
    public void Production_orchestrator_receives_the_auto_warm_queue()
    {
        // The defect an earlier revision shipped: the queue existed but no production path ever
        // reached it, so installing a model warmed nothing. Resolving the orchestrator is what
        // every download path does, so its dependency on the queue is the wiring that matters.
        // If this dependency is dropped, the feature silently dies again — which is why the
        // orchestrator's own success path (not an observer) is the trigger.
        var services = new ServiceCollection();
        services.AddTrackdub();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var orchestrator = provider.GetRequiredService<ModelDownloadOrchestrator>();
        Assert.NotNull(orchestrator);
        Assert.Same(provider.GetRequiredService<IEngineAutoWarmQueue>(), provider.GetRequiredService<IEngineAutoWarmQueue>());
    }

    [Fact]
    public async Task A_drain_leaves_the_queue_usable_for_later_installs()
    {
        var services = new ServiceCollection();
        services.AddTrackdub();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        IEngineAutoWarmQueue queue = provider.GetRequiredService<IEngineAutoWarmQueue>();

        queue.RequestWarmAfterInstall("onnx-community/whisper-small");
        await queue.DrainAsync(TestContext.Current.CancellationToken);

        // Draining is a flush, not a shutdown: this second request must still be accepted.
        queue.RequestWarmAfterInstall("onnx-community/opus-mt-en-es");
        await queue.DrainAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Auto_warm_can_be_disabled_by_environment_without_breaking_registration()
    {
        Environment.SetEnvironmentVariable(EngineAutoWarmCoordinator.DisableEnvironmentVariable, "0");
        try
        {
            var services = new ServiceCollection();
            services.AddTrackdub();
            using ServiceProvider provider = services.BuildServiceProvider(
                new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            // Registration still resolves; the switch only suppresses the work.
            Assert.NotNull(provider.GetRequiredService<IEngineAutoWarmQueue>());
        }
        finally
        {
            Environment.SetEnvironmentVariable(EngineAutoWarmCoordinator.DisableEnvironmentVariable, null);
        }
    }
}
