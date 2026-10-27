using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Trackdub.Application.Pipeline;
using Trackdub.Composition.Pipeline;
using Trackdub.Composition.Runtime;
using Trackdub.Composition.Tts;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Domain;
using Trackdub.Licensing;
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
    public void Production_orchestrator_receives_the_voice_catalog_holder()
    {
        // The refresh-on-install wiring dies silently if the holder is never passed: the
        // orchestrator's refresh returns at its null guard and no test resolving the
        // orchestrator directly would notice. Resolve through AddTrackdub so the
        // production construction site itself is under test.
        var services = new ServiceCollection();
        services.AddTrackdub();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        var orchestrator = provider.GetRequiredService<ModelDownloadOrchestrator>();
        Assert.Same(provider.GetRequiredService<KokoroVoiceCatalogHolder>(), orchestrator.VoiceCatalogHolder);
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

    [Fact]
    public async Task Installer_awaits_license_initialization_before_host_activation()
    {
        var queue = new RecordingQueue();
        var initializer = new RecordingLicenseInitializer();
        var installer = new EngineAutoWarmInstaller(queue, initializer);

        await installer.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(initializer.WasInitialized);
        Assert.True(queue.IsHostStarted);
    }

    [Fact]
    public async Task Installer_awaits_ep_context_hardware_identity_before_host_activation()
    {
        // Session creation consumes the EP-context hardware identity without probing, so
        // host startup must load it before marking the host started (the point after which
        // pooled sessions may be created). Order matters, not just that both happen.
        var order = new List<string>();
        var queue = new RecordingQueue(order);
        var warmup = new RecordingEpContextWarmupService(order);
        var installer = new EngineAutoWarmInstaller(queue, epContextWarmupService: warmup);

        await installer.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(warmup.WasInitialized);
        Assert.Equal(["ep-context-hardware-identity", "host-started"], order);
    }

    private sealed class RecordingEpContextWarmupService(List<string> order) : IEpContextWarmupService
    {
        public bool WasInitialized { get; private set; }

        public Task InitializeHardwareIdentityAsync(CancellationToken cancellationToken = default)
        {
            WasInitialized = true;
            order.Add("ep-context-hardware-identity");
            return Task.CompletedTask;
        }

        public Task<EpContextWarmReport> WarmAsync(
            IReadOnlyList<string>? sourceModelPaths = null,
            IProgress<string>? progress = null,
            ExecutionProviderKind provider = ExecutionProviderKind.TensorRTRtx,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingLicenseInitializer : ILicenseInitializer
    {
        public bool WasInitialized { get; private set; }

        public Task<LicenseValidationResult> InitializeAsync(CancellationToken cancellationToken = default)
        {
            WasInitialized = true;
            return Task.FromResult(new LicenseValidationResult(LicenseTier.Free, null, 0, 0, null, null));
        }
    }

    private sealed class RecordingQueue(List<string>? order = null) : IEngineAutoWarmQueue
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
            order?.Add("host-started");
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
