using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Composition.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;
using Xunit;

namespace Trackdub.Composition.Tests.Pipeline;

public class ResidentWarmupServiceTests
{
    private readonly RecordingCoordinator coordinator = new();
    private readonly List<ExecutionProviderKind> bootstrapped = [];
    private readonly FakeExecutionProviderDiscovery discovery = new(
    [
        new ExecutionProviderAvailability(ExecutionProviderKind.Cpu, IsAvailable: true),
        new ExecutionProviderAvailability(ExecutionProviderKind.DirectMl, IsAvailable: true),
        new ExecutionProviderAvailability(ExecutionProviderKind.Qnn, IsAvailable: false),
        new ExecutionProviderAvailability(ExecutionProviderKind.TensorRTRtx, IsAvailable: true),
        // Reachable only through the inference worker: never bootstrapped in-process.
        new ExecutionProviderAvailability(ExecutionProviderKind.Cuda, IsAvailable: true, EngineFamilies: ["kokoro"]),
    ]);

    [Fact]
    public async Task WarmAsync_BootstrapsInProcessProvidersInProbeOrder_ThenWarmsStagesInRequestOrder()
    {
        ResidentWarmupReport report = await CreateService().WarmAsync(
            Request([RuntimeStage.Asr, RuntimeStage.Translation, RuntimeStage.Tts]),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(report.Skipped);
        Assert.Equal([ExecutionProviderKind.TensorRTRtx, ExecutionProviderKind.DirectMl], bootstrapped);
        Assert.Equal([RuntimeStage.Asr, RuntimeStage.Translation, RuntimeStage.Tts], coordinator.Stages);
        Assert.All(report.Stages, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal("de", Assert.Single(coordinator.Requests.Select(request => request.TargetLanguageCode).Distinct()));
    }

    [Fact]
    public async Task WarmAsync_StageFailuresAreReportedAndDoNotStopLaterStages()
    {
        coordinator.Results[RuntimeStage.Asr] = new StageWarmupResult(Attempted: true, Succeeded: false, "model missing");
        coordinator.Throws.Add(RuntimeStage.Translation);

        ResidentWarmupReport report = await CreateService().WarmAsync(
            Request([RuntimeStage.Asr, RuntimeStage.Translation, RuntimeStage.Tts]),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([false, false, true], report.Stages.Select(outcome => outcome.Succeeded));
        Assert.Equal("model missing", report.Stages[0].Detail);
        Assert.Equal("boom", report.Stages[1].Detail);
    }

    [Fact]
    public async Task WarmAsync_ProviderBootstrapFailureIsReportedAndStagesStillWarm()
    {
        var service = CreateService(bootstrap: (provider, _) => provider is ExecutionProviderKind.DirectMl
            ? throw new InvalidOperationException("no adapter")
            : Task.FromResult(new ResidentProviderBootstrapOutcome(provider, Ready: true, null)));

        ResidentWarmupReport report = await service.WarmAsync(
            Request([RuntimeStage.Asr]), cancellationToken: TestContext.Current.CancellationToken);

        ResidentProviderBootstrapOutcome failed = Assert.Single(report.Providers, outcome => !outcome.Ready);
        Assert.Equal((ExecutionProviderKind.DirectMl, "no adapter"), (failed.Provider, failed.Detail));
        Assert.True(Assert.Single(report.Stages).Succeeded);
    }

    [Fact]
    public async Task WarmAsync_WithoutProviderBootstrap_SkipsDiscovery()
    {
        await CreateService().WarmAsync(
            Request([RuntimeStage.Asr]) with { BootstrapProviders = false },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(bootstrapped);
        Assert.Equal([RuntimeStage.Asr], coordinator.Stages);
    }

    [Theory]
    [InlineData("0")]
    [InlineData(" false ")]
    public async Task WarmAsync_KillSwitch_SkipsEverything(string value)
    {
        ResidentWarmupReport report = await CreateService(environment: value).WarmAsync(
            Request(ResidentWarmupRequest.DefaultStages), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.Skipped);
        Assert.Empty(bootstrapped);
        Assert.Empty(coordinator.Stages);
    }

    [Fact]
    public async Task WarmAsync_CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        coordinator.OnWarm = _ => cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateService().WarmAsync(
            Request([RuntimeStage.Asr, RuntimeStage.Tts]), cancellationToken: cancellation.Token));

        Assert.Equal([RuntimeStage.Asr], coordinator.Stages);
    }

    [Fact]
    public void CompositionRoot_RegistersTheService()
    {
        var services = new ServiceCollection();
        services.AddTrackdub();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<ResidentWarmupService>(provider.GetRequiredService<IResidentWarmupService>());
    }

    private ResidentWarmupService CreateService(
        Func<ExecutionProviderKind, CancellationToken, Task<ResidentProviderBootstrapOutcome>>? bootstrap = null,
        string? environment = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IStageWarmupCoordinator>(_ => coordinator);
        ServiceProvider provider = services.BuildServiceProvider();
        return new ResidentWarmupService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            discovery,
            new FakeHardwareProfileProvider(),
            async (kind, cancellationToken) =>
            {
                bootstrapped.Add(kind);
                return bootstrap is null
                    ? new ResidentProviderBootstrapOutcome(kind, Ready: true, null)
                    : await bootstrap(kind, cancellationToken);
            },
            _ => environment);
    }

    private static ResidentWarmupRequest Request(IReadOnlyList<RuntimeStage> stages) =>
        new(
            stages,
            new RuntimeModelSelections(
                AsrModelOverride.Auto,
                IsDevBuild: false,
                HardwareOverrides: new Dictionary<string, ExecutionProviderKind>()),
            SourceLanguageCode: "en",
            TargetLanguageCode: "de");

    private sealed class RecordingCoordinator : IStageWarmupCoordinator
    {
        public List<StageWarmupRequest> Requests { get; } = [];
        public List<RuntimeStage> Stages => Requests.Select(request => request.Stage).ToList();
        public Dictionary<RuntimeStage, StageWarmupResult> Results { get; } = [];
        public HashSet<RuntimeStage> Throws { get; } = [];
        public Action<RuntimeStage>? OnWarm { get; set; }

        public Task<StageWarmupResult> WarmAsync(StageWarmupRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            OnWarm?.Invoke(request.Stage);
            if (Throws.Contains(request.Stage))
            {
                throw new InvalidOperationException("boom");
            }

            return Task.FromResult(Results.GetValueOrDefault(request.Stage, new StageWarmupResult(Attempted: true, Succeeded: true)));
        }
    }
}
