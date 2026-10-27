using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Composition;
using Trackdub.Composition.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;
using Xunit;

namespace Trackdub.Composition.Tests.Pipeline;

public class RuntimeStageWarmupCoordinatorTests
{
    private static RuntimeModelSelections Selections(
        IReadOnlyDictionary<string, ExecutionProviderKind>? hardwareOverrides = null,
        string? translationModelAlias = null) =>
        new(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: hardwareOverrides ?? new Dictionary<string, ExecutionProviderKind>(),
            TranslationModelAlias: translationModelAlias);

    private static StageRuntimePlan RunnablePlan(StageRuntimePlanningRequest request) =>
        new()
        {
            Stage = request.Stage,
            Status = StageRuntimePlanStatus.Verified,
            ModelId = "model-x",
            ModelAlias = "alias-x",
            EngineFamily = "family-x",
            Variant = "fp16",
            ExecutionProvider = ExecutionProviderKind.DirectMl,
            ModelRootPath = "/models/x",
            ModelEntryPath = "/models/x/graph.onnx",
            ModelRevisionHash = "deadbeef",
        };

    [Fact]
    public async Task WarmAsync_Translation_RoutesSelectionsThroughPlannerIntoOneSmoke()
    {
        StageRuntimePlanningRequest? capturedPlan = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedPlan = req;
                return RunnablePlan(req);
            },
        };
        ExecutionProviderSmokeTestRequest? capturedSmoke = null;
        var smoke = new FakeExecutionProviderSmokeTester(req =>
        {
            capturedSmoke = req;
            return new ExecutionProviderSmokeTestResult(true);
        });
        var capture = new BenchmarkPhaseCapture();
        using IDisposable? activation = BenchmarkPhaseCapture.Activate(capture);
        var coordinator = new RuntimeStageWarmupCoordinator(planner, smoke);

        StageWarmupResult result = await coordinator.WarmAsync(
            new StageWarmupRequest(
                RuntimeStage.Translation,
                Selections(
                    new Dictionary<string, ExecutionProviderKind>
                    {
                        ["Translation"] = ExecutionProviderKind.DirectMl,
                    },
                    translationModelAlias: "opus-mt-en-es"),
                SourceLanguageCode: "pt-BR",
                TargetLanguageCode: "es-MX"),
            CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.True(result.Succeeded);
        Assert.NotNull(capturedPlan);
        Assert.Equal(RuntimeStage.Translation, capturedPlan!.Stage);
        Assert.Equal("opus-mt-en-es", capturedPlan.PreferredModelAlias);
        Assert.True(capturedPlan.RequirePreferredModelAlias);
        Assert.Equal("pt-BR", capturedPlan.SourceLanguage);
        Assert.Equal("es-MX", capturedPlan.TargetLanguage);
        Assert.Equal(ExecutionProviderKind.DirectMl, capturedPlan.PreferredExecutionProvider);

        Assert.NotNull(capturedSmoke);
        Assert.Equal(RuntimeStage.Translation, capturedSmoke!.Stage);
        Assert.Equal("model-x", capturedSmoke.ModelId);
        Assert.Equal("alias-x", capturedSmoke.ModelAlias);
        Assert.Equal("family-x", capturedSmoke.EngineFamily);
        Assert.Equal("fp16", capturedSmoke.Variant);
        Assert.Equal(ExecutionProviderKind.DirectMl, capturedSmoke.ExecutionProvider);
        Assert.Equal("/models/x", capturedSmoke.ModelRootPath);
        Assert.Equal("/models/x/graph.onnx", capturedSmoke.EntryPath);
        Assert.Equal("deadbeef", capturedSmoke.ModelRevisionHash);

        IReadOnlyDictionary<string, long> counters = capture.SnapshotCounters();
        Assert.Equal(1L, counters["stageWarmupAttempt"]);
        Assert.Equal(1L, counters["stageWarmupSuccess"]);
        Assert.False(counters.ContainsKey("stageWarmupFailure"));
    }

    [Fact]
    public async Task WarmAsync_NonRunnablePlan_AttemptsNothingAndNeverSmokes()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req => new StageRuntimePlan
            {
                Stage = req.Stage,
                Status = StageRuntimePlanStatus.DownloadRequired,
                Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "cache miss"),
            },
        };
        int smokeCalls = 0;
        var smoke = new FakeExecutionProviderSmokeTester(_ =>
        {
            smokeCalls++;
            return new ExecutionProviderSmokeTestResult(true);
        });
        var coordinator = new RuntimeStageWarmupCoordinator(planner, smoke);

        StageWarmupResult result = await coordinator.WarmAsync(
            new StageWarmupRequest(RuntimeStage.Vad, Selections()),
            CancellationToken.None);

        Assert.False(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Contains("no runnable plan", result.Detail);
        Assert.Equal(0, smokeCalls);
    }

    [Fact]
    public async Task WarmAsync_SmokeFailure_ReturnsFailedResultWithoutThrowing()
    {
        var planner = new FakeRuntimePlanner { PlanHandler = RunnablePlan };
        var smoke = new FakeExecutionProviderSmokeTester(
            _ => new ExecutionProviderSmokeTestResult(false, "provider failed"));
        var coordinator = new RuntimeStageWarmupCoordinator(planner, smoke);

        StageWarmupResult result = await coordinator.WarmAsync(
            new StageWarmupRequest(RuntimeStage.Asr, Selections()),
            CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal("provider failed", result.Detail);
    }

    [Fact]
    public async Task WarmAsync_PlannerException_IsConvertedToFailedResult()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandlerAsync = (_, _) =>
                Task.FromException<StageRuntimePlan>(new InvalidOperationException("planner blew up")),
        };
        var smoke = new FakeExecutionProviderSmokeTester();
        var coordinator = new RuntimeStageWarmupCoordinator(planner, smoke);

        StageWarmupResult result = await coordinator.WarmAsync(
            new StageWarmupRequest(RuntimeStage.Asr, Selections()),
            CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal("planner blew up", result.Detail);
    }

    [Fact]
    public async Task WarmAsync_Cancellation_Propagates()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandlerAsync = (_, ct) =>
                Task.FromCanceled<StageRuntimePlan>(ct),
        };
        var coordinator = new RuntimeStageWarmupCoordinator(
            planner, new FakeExecutionProviderSmokeTester());

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.WarmAsync(
                new StageWarmupRequest(RuntimeStage.Asr, Selections()), cts.Token));
    }

    [Fact]
    public async Task WarmAsync_TtsVoiceClone_UsesCloneAliasRoute()
    {
        StageRuntimePlanningRequest? capturedPlan = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = req =>
            {
                capturedPlan = req;
                return RunnablePlan(req);
            },
        };
        var coordinator = new RuntimeStageWarmupCoordinator(
            planner, new FakeExecutionProviderSmokeTester());

        await coordinator.WarmAsync(
            new StageWarmupRequest(RuntimeStage.Tts, Selections(), RequiresVoiceClone: true),
            CancellationToken.None);

        Assert.NotNull(capturedPlan);
        Assert.Equal(RuntimeStage.Tts, capturedPlan!.Stage);
        Assert.Equal(VoiceCloningDefaults.ChatterboxPrimaryAlias, capturedPlan.PreferredModelAlias);
        Assert.True(capturedPlan.RequirePreferredModelAlias);
    }

    [Fact]
    public async Task WarmAsync_PassedSmoke_RecordsVerdictForNextLaunch()
    {
        var planner = new FakeRuntimePlanner { PlanHandler = RunnablePlan };
        var smoke = new FakeExecutionProviderSmokeTester(
            _ => new ExecutionProviderSmokeTestResult(true));
        var verdictStore = new RecordingVerdictStore();
        var coordinator = new RuntimeStageWarmupCoordinator(
            planner, smoke, smokeVerdictStore: verdictStore,
            hardwareProfileProvider: new FakeHardwareProfileProvider());

        StageWarmupResult result = await coordinator.WarmAsync(
            new StageWarmupRequest(RuntimeStage.Asr, Selections()),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        SmokeVerdictKey key = Assert.Single(verdictStore.Recorded);
        Assert.Equal("deadbeef", key.ModelSha256);
        Assert.Equal(ExecutionProviderKind.DirectMl, key.ExecutionProvider);
        Assert.Equal("1.24.0-test", key.OrtRuntimeVersion);
    }

    [Fact]
    public async Task WarmAsync_FailedSmoke_RecordsNoVerdictAndReturnsFailureWithReason()
    {
        var planner = new FakeRuntimePlanner { PlanHandler = RunnablePlan };
        var smoke = new FakeExecutionProviderSmokeTester(
            _ => new ExecutionProviderSmokeTestResult(false, "provider failed"));
        var verdictStore = new RecordingVerdictStore();
        var coordinator = new RuntimeStageWarmupCoordinator(
            planner, smoke, smokeVerdictStore: verdictStore,
            hardwareProfileProvider: new FakeHardwareProfileProvider());

        StageWarmupResult result = await coordinator.WarmAsync(
            new StageWarmupRequest(RuntimeStage.Asr, Selections()),
            CancellationToken.None);

        Assert.True(result.Attempted);
        Assert.False(result.Succeeded);
        Assert.Equal("provider failed", result.Detail);
        Assert.Empty(verdictStore.Recorded);
    }

    private sealed class RecordingVerdictStore : ISmokeVerdictStore
    {
        public List<SmokeVerdictKey> Recorded { get; } = [];
        public bool IsVerified(SmokeVerdictKey key) => Recorded.Contains(key);
        public void RecordVerified(SmokeVerdictKey key) => Recorded.Add(key);
        public void Clear() => Recorded.Clear();
    }

    private sealed class FakeHardwareProfileProvider : IHardwareProfileProvider
    {
        public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new HardwareProfile(
                "windows", "x64", HasGpu: true, GpuDescription: "Test GPU",
                NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Ada,
                GpuDriverVersion: "555.1",
                OrtRuntimeVersion: "1.24.0-test"));
    }

    [Fact]
    public void CompositionRoot_ResolvesStageWarmupCoordinator_InScope()
    {
        var services = new ServiceCollection();
        services.AddTrackdub();
        using ServiceProvider provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        IStageWarmupCoordinator coordinator =
            scope.ServiceProvider.GetRequiredService<IStageWarmupCoordinator>();
        Assert.IsType<RuntimeStageWarmupCoordinator>(coordinator);
    }
}
