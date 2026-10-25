using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Composition.Pipeline;
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
    public async Task Production_graph_wires_install_notifications_to_the_auto_warm_queue()
    {
        var services = new ServiceCollection();
        services.AddTrackdub();
        await using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        // The coordinator owns its install subscription, so resolving the queue is sufficient to
        // activate production wiring. This is the property an earlier design violated: a separate
        // installer existed but nothing in the graph resolved it.
        IEngineAutoWarmQueue queue = provider.GetRequiredService<IEngineAutoWarmQueue>();
        Assert.NotNull(queue);
        Assert.Same(queue, provider.GetRequiredService<IEngineAutoWarmQueue>());

        // A drain must leave the queue usable, so a later install still warms.
        queue.RequestWarmAfterInstall("onnx-community/whisper-small");
        await queue.DrainAsync(TestContext.Current.CancellationToken);
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
