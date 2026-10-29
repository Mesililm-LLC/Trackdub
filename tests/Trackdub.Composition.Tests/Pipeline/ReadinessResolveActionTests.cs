using System.Text.Json;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Dubbing;
using Trackdub.Composition.Pipeline;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.Pipeline;

public sealed class ReadinessResolveActionTests
{
    [Theory]
    [InlineData("auto", null)]
    [InlineData("shipped-default", null)]
    [InlineData("serialized-default", null)]
    [InlineData("generated-alias", null)]
    [InlineData("caller", "download")]
    [InlineData("caller-default", "download")]
    [InlineData("saved", "download")]
    [InlineData("saved-default", "download")]
    [InlineData("pack", "download")]
    [InlineData("override", "download")]
    [InlineData("legacy-configured-default", "download")]
    [InlineData("provider-only", null)]
    public async Task EvaluateAsync_uses_request_provenance_even_when_planner_resolves_a_different_alias(string origin, string? action)
    {
        var settings = StudioSettings.Default with { TranslationModelOverride = TranslationModelOverride.Auto };
        var selections = origin switch
        {
            "shipped-default" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(StudioSettings.Default),
            "serialized-default" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(StudioSettings.Default))!),
            "generated-alias" => Automatic() with { TranslationModelAlias = "application-default", SelectionIntents = new Dictionary<RuntimeStage, RuntimeModelSelectionIntent> { [RuntimeStage.Translation] = RuntimeModelSelectionIntent.Automatic } },
            "caller" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings, new InferenceModelPreferences(TranslationModelAlias: "caller-model")),
            "caller-default" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(StudioSettings.Default, new InferenceModelPreferences(TranslationModelAlias: TranslationModelOverrideSettings.MadladModelAlias)),
            "saved" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings with { StageModelAliases = new Dictionary<string, string> { [StageNames.Translation] = "saved-model" } }),
            "saved-default" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(StudioSettings.Default with { StageModelAliases = new Dictionary<string, string> { [StageNames.Translation] = TranslationModelOverrideSettings.MadladModelAlias } }),
            "pack" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings with { AppliedStarterPackId = "pack", StageModelAliases = new Dictionary<string, string> { [StageNames.Translation] = "pack-model" } }),
            "override" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings with { TranslationModelOverride = TranslationModelOverride.Madlad, AutomaticModelAliases = null }),
            "legacy-configured-default" => RuntimeModelRequestFactory.CreateSelectionsFromSettings(StudioSettings.Default with { AutomaticModelAliases = null }),
            "provider-only" => Automatic() with { HardwareOverrides = new Dictionary<string, ExecutionProviderKind> { [StageNames.Translation] = ExecutionProviderKind.Cpu } },
            _ => Automatic()
        };
        var planner = new Planner();
        var service = Service(planner);
        var report = await service.EvaluateAsync([RuntimeStage.Translation], selections, state: null, cancellationToken: TestContext.Current.CancellationToken);
        StageReadiness stage = Assert.Single(report.Stages);
        Assert.Equal(action, stage.ResolveAction);
        if (action is null)
        {
            Assert.Equal(ReadinessState.PendingTranslationResolution, stage.Status);
            Assert.Null(stage.ModelId);
            Assert.Null(stage.ModelAlias);
            Assert.Equal(0, planner.Calls);
            return;
        }
        Assert.Equal(ReadinessState.DownloadRequired, stage.Status);
        Assert.Equal("resolved-model", stage.ModelId);
        Assert.Equal("planner-fallback", stage.ModelAlias);
    }

    [Fact]
    public async Task EvaluateAsync_cache_identity_includes_selection_intent()
    {
        var planner = new Planner();
        var service = Service(planner);
        var automatic = Automatic() with { TranslationModelAlias = "same-alias", SelectionIntents = new Dictionary<RuntimeStage, RuntimeModelSelectionIntent> { [RuntimeStage.Translation] = RuntimeModelSelectionIntent.Automatic } };
        var explicitSelection = automatic with { SelectionIntents = new Dictionary<RuntimeStage, RuntimeModelSelectionIntent> { [RuntimeStage.Translation] = RuntimeModelSelectionIntent.Explicit } };
        var first = await service.EvaluateAsync([RuntimeStage.Translation], automatic, null, TestContext.Current.CancellationToken);
        var second = await service.EvaluateAsync([RuntimeStage.Translation], explicitSelection, null, TestContext.Current.CancellationToken);
        Assert.Equal(ReadinessState.PendingTranslationResolution, Assert.Single(first.Stages).Status);
        Assert.Equal("download", Assert.Single(second.Stages).ResolveAction);
        Assert.Equal(1, planner.Calls);
    }

    [Fact]
    public async Task Caller_alias_overlaid_on_host_automatic_selections_is_explicit_through_EvaluateAsync()
    {
        var automatic = RuntimeModelRequestFactory.CreateSelectionsFromSettings(StudioSettings.Default);
        var overlaid = DubbingModelPreferences.ApplyModelPreferenceAliases(automatic, new InferenceModelPreferences(TranslationModelAlias: "caller-model"));
        var report = await Service(new Planner()).EvaluateAsync([RuntimeStage.Translation], overlaid, null, TestContext.Current.CancellationToken);
        Assert.Equal("download", Assert.Single(report.Stages).ResolveAction);
    }

    [Fact]
    public async Task Applied_starter_pack_stage_pin_is_explicit_through_EvaluateAsync()
    {
        var pack = await new StarterPackCatalog().GetAsync("basic", TestContext.Current.CancellationToken);
        var profile = StarterPackResolver.ResolveProfile(pack, "default");
        var settings = StarterPackApplyService.BuildUpdatedSettings(StudioSettings.Default, pack, profile,
            StarterPackApplyContract.Resolve(pack.Id, profile.Id), StarterPackHardwareProfile.BalancedGpu);
        var selections = RuntimeModelRequestFactory.CreateSelectionsFromSettings(settings);
        var report = await Service(new Planner()).EvaluateAsync([RuntimeStage.Translation], selections, null, TestContext.Current.CancellationToken);
        Assert.NotNull(selections.TranslationModelAlias);
        Assert.Equal("download", Assert.Single(report.Stages).ResolveAction);
    }

    [Theory]
    [InlineData(StageRuntimePlanStatus.DownloadRequired)]
    [InlineData(StageRuntimePlanStatus.Blocked)]
    public async Task Integrity_repair_remains_download_for_automatic_selection(StageRuntimePlanStatus status)
    {
        var planner = new Planner { Status = status, Code = RuntimePlanFallbackCode.ModelIntegrityMismatch };
        var report = await Service(planner).EvaluateAsync([RuntimeStage.Asr], Automatic(), null, TestContext.Current.CancellationToken);
        Assert.Equal("download", Assert.Single(report.Stages).ResolveAction);
    }

    private static RuntimeModelSelections Automatic() => new(AsrModelOverride.Auto, false, new Dictionary<string, ExecutionProviderKind>());
    private static PipelineReadinessService Service(IRuntimePlanner planner) => new(planner, new NullCloudApiKeyProvider(), new FakeConsentService());

    private sealed class NullCloudApiKeyProvider : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }
    private sealed class Planner : IRuntimePlanner
    {
        public int Calls { get; private set; }
        public StageRuntimePlanStatus Status { get; init; } = StageRuntimePlanStatus.DownloadRequired;
        public RuntimePlanFallbackCode Code { get; init; } = RuntimePlanFallbackCode.ModelNotCached;
        public Task<StageRuntimePlan> PlanAsync(StageRuntimePlanningRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new StageRuntimePlan { Stage = request.Stage, Status = Status, ModelId = "resolved-model", ModelAlias = "planner-fallback", Fallback = new(Code) });
        }
    }
}
