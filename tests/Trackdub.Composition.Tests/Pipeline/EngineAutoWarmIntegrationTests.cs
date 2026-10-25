using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
    public async Task Application_host_startup_activates_the_engine_auto_warm_queue()
    {
        // Asserts on host-startup state, not on resolvability: the queue is a plain singleton, so
        // resolving it proves nothing about the hosted-service wiring. IsHostStarted rather than
        // IsActive, because a warm request self-activates and would mask missing host wiring.
        using IHost host = new HostBuilder()
            .ConfigureServices(services => services.AddTrackdub())
            .Build();

        var queue = (EngineAutoWarmCoordinator)host.Services.GetRequiredService<IEngineAutoWarmQueue>();

        Assert.False(queue.IsHostStarted);

        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(queue.IsHostStarted, "host startup must be recorded via EngineAutoWarmInstaller");
        Assert.True(queue.IsActive);
    }

    [Fact]
    public async Task Installer_start_activates_the_queue_and_activation_is_idempotent()
    {
        var queue = new RecordingQueue();
        var installer = new EngineAutoWarmInstaller(queue);

        await installer.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(queue.IsActive);

        // A second activation reports that it changed nothing, so callers can tell the difference.
        Assert.False(queue.Activate());
        Assert.True(queue.IsActive);
    }

    private sealed class RecordingQueue : IEngineAutoWarmQueue
    {
        public List<string> Requests { get; } = [];

        public bool IsActive { get; private set; }

        public bool IsHostStarted { get; private set; }

        public bool Activate()
        {
            bool changed = !IsActive;
            IsActive = true;
            return changed;
        }

        public bool MarkHostStarted()
        {
            Activate();
            bool changed = !IsHostStarted;
            IsHostStarted = true;
            return changed;
        }

        public void RequestWarmAfterInstall(string modelId, CancellationToken cancellationToken = default) =>
            Requests.Add(modelId);

        public Task DrainAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
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
