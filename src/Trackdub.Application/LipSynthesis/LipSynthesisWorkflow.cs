using Trackdub.Application.Artifacts;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.Artifacts;

namespace Trackdub.Application.LipSynthesis;

/// <summary>Request to run the M23 lip-synthesis stage for the current project.</summary>
public sealed record LipSynthesisRunRequest(
    bool IsLicenseApproved = true,
    bool AllowExperimentalExecution = false,
    string? PreferredModelAlias = null);

/// <summary>
/// Orchestrates the M23 video lip-synthesis stage over the current project state. Builds per-speaker
/// turns from diarization, runs the gated experimental LatentSync engine, and reloads
/// the per-turn results. User-triggered — never auto-runs on media load. Mirrors LipSyncWorkflow.
/// </summary>
public sealed class LipSynthesisWorkflow(
    TranscriptProjectStateService stateService,
    LipSynthesisStageHandler lipSynthesisStageHandler,
    IArtifactStore artifactStore)
    : IDisposable
{
    private const string ConcurrentOperationMessage = "A lip-synthesis operation is already running for this project.";

    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly LipSynthesisStageHandler lipSynthesisStageHandler = lipSynthesisStageHandler ?? throw new ArgumentNullException(nameof(lipSynthesisStageHandler));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public void Dispose() => operationGate.Dispose();

    public async Task<TranscriptProjectState> SynthesizeAllAsync(
        LipSynthesisRunRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        cancellationToken.ThrowIfCancellationRequested();
        if (!operationGate.Wait(0))
            throw new InvalidOperationException(ConcurrentOperationMessage);

        try
        {
            TranscriptProjectState currentState = await stateService
                .OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken)
                .ConfigureAwait(false);

            // Prerequisite: a media asset that actually has video.
            if (currentState.ProjectState.MediaAsset is not { } mediaAsset || !mediaAsset.HasVideo)
                return currentState;

            // Mirror LipSync: record an honest skipped stage run when dubbing prerequisites are missing.
            if (!TranscriptWorkflowUtilities.HasGeneratedTtsTakes(currentState))
            {
                var emptyRequest = new LipSynthesisStageRequest(
                    ProjectId: currentState.ProjectState.Project.Id,
                    MediaAsset: mediaAsset,
                    SourceVideoPath: mediaAsset.SourceFilePath,
                    DubbedAudioPath: string.Empty,
                    SpeakerTurns: [],
                    IsEnabled: true,
                    IsLicenseApproved: request.IsLicenseApproved,
                    AllowExperimentalExecution: request.AllowExperimentalExecution,
                    PreferredModelAlias: request.PreferredModelAlias);

                await lipSynthesisStageHandler
                    .HandleAsync(emptyRequest, cancellationToken).ConfigureAwait(false);

                return await stateService
                    .OpenAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken)
                    .ConfigureAwait(false);
            }

            string sourceVideoPath = mediaAsset.SourceFilePath;
            ProjectArtifact? dubbedDriver = TranscriptWorkflowUtilities
                .ResolveLipSynthesisDriverAudioArtifact(currentState);
            string dubbedAudioPath = dubbedDriver is not null
                ? artifactStore.GetPath(dubbedDriver.RelativePath)
                : string.Empty;

            // Process by diarization speaker turn (not the whole file at once).
            var turns = currentState.SpeakerTurns
                .Select(t => new LipSynthesisTurn(
                    SegmentId: t.Id,
                    Start: TimeSpan.FromSeconds(t.StartSeconds),
                    End: TimeSpan.FromSeconds(t.EndSeconds),
                    SpeakerId: t.SpeakerId.ToString("D")))
                .ToList();

            var stageRequest = new LipSynthesisStageRequest(
                ProjectId: currentState.ProjectState.Project.Id,
                MediaAsset: mediaAsset,
                SourceVideoPath: sourceVideoPath,
                DubbedAudioPath: dubbedAudioPath,
                SpeakerTurns: turns,
                IsEnabled: true,
                IsLicenseApproved: request.IsLicenseApproved,
                AllowExperimentalExecution: request.AllowExperimentalExecution,
                PreferredModelAlias: request.PreferredModelAlias);

            await lipSynthesisStageHandler
                .HandleAsync(stageRequest, cancellationToken).ConfigureAwait(false);

            return await stateService
                .OpenAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }
}
