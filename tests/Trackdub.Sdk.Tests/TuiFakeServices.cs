using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

internal sealed class FakeModelInventoryService : IModelInventoryService
{
    public Task<IReadOnlyList<ModelInventoryEntry>> GetAllAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModelInventoryEntry>>([]);

    public Task<ModelInventoryEntry?> GetByModelIdAsync(
        string modelId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ModelInventoryEntry?>(null);
}

internal sealed class FakeStarterPackPresentationService : IStarterPackPresentationService
{
    private static readonly StarterPackSummary Summary = new(
        "basic",
        "Basic / Fast",
        "fast",
        ["default"],
        RequiredCount: 1,
        InstalledCount: 0,
        CanApply: false,
        HasCommercialVerificationGap: false,
        RequiresVoiceCloningConsent: false,
        Recommended: false,
        Applied: false,
        BlockedReason: "Download required.",
        StatusLabel: "download first");

    public Task<IReadOnlyList<StarterPackSummary>> ListSummariesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StarterPackSummary>>([Summary]);

    public Task<StarterPackSummary> GetSummaryAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Summary);

    public Task<string?> GetRecommendedPackIdAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    public Task<bool> RequiresVoiceCloningConsentAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    public Task<IReadOnlyList<string>> GetRunnablePackIdsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);
}

internal sealed class FakePipelineReadinessService : IPipelineReadinessService
{
    public Task<PipelineReadinessReport> EvaluateAsync(
        IReadOnlyList<RuntimeStage> enabledStages,
        RuntimeModelSelections selections,
        TranscriptProjectState? state,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null,
        bool validateRuntime = true,
        IReadOnlyDictionary<string, bool>? explicitEgressConsents = null) =>
        Task.FromResult(PipelineReadinessReport.Empty);

    public void InvalidateCache(IReadOnlyList<RuntimeStage>? stages = null)
    {
    }
}
