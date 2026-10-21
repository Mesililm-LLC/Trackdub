namespace Trackdub.Application.LipSync;

using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;

public sealed class LipSyncWorkflow(
    TranscriptProjectStateService stateService,
    LipSyncStageHandler lipSyncStageHandler,
    IArtifactStore artifactStore)
    : IDisposable
{
    private const string ConcurrentOperationMessage = "A lip-sync operation is already running for this project.";

    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly LipSyncStageHandler lipSyncStageHandler = lipSyncStageHandler ?? throw new ArgumentNullException(nameof(lipSyncStageHandler));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly SemaphoreSlim operationGate = new(1, 1);

    public void Dispose() => operationGate.Dispose();

    /// <summary>
    /// Run the full lip-sync alignment and stretch pass for all takes in the current project state.
    /// The stage handler persists accepted segments before finalizing the stage run.
    /// Returns the refreshed project state.
    /// </summary>
    public async Task<TranscriptProjectState> AlignAllAsync(
        LipSyncAlignAllRequest request,
        CancellationToken cancellationToken)
    {
        return await RunExclusiveAsync(
            async token =>
            {
                TranscriptProjectState currentState = await OpenAsync(token).ConfigureAwait(false);

                // Guard: no media asset → cannot run lip-sync.
                if (currentState.ProjectState.MediaAsset is null)
                    return currentState;

                // When media exists but no TTS takes have been produced yet, still
                // invoke the handler so it records a skipped stage run (with a clear
                // prerequisite-not-met message) instead of silently returning the
                // state unchanged, which would leave pipeline history with no evidence
                // that lip-sync was ever requested.
                if (currentState.TtsTakes.Count == 0)
                {
                    var emptyTakeRequest = new LipSyncStageRequest(
                        ProjectId: currentState.ProjectState.Project.Id,
                        MediaAsset: currentState.ProjectState.MediaAsset,
                        TtsTakes: currentState.TtsTakes,
                        ExistingArtifacts: currentState.ProjectState.Artifacts,
                        IsEnabled: true,
                        PreferredModelAlias: request.PreferredModelAlias,
                        StretchBounds: request.StretchBounds,
                        SegmentTranscriptMap: new Dictionary<Guid, string>(),
                        SourceLanguageCode: currentState.TranscriptLanguage,
                        TargetLanguageCode: currentState.SelectedTranslationTargetLanguage);

                    await lipSyncStageHandler
                        .HandleAsync(emptyTakeRequest, token).ConfigureAwait(false);

                    return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token)
                        .ConfigureAwait(false);
                }

                Dictionary<Guid, string> segmentTranscriptMap = currentState.TranslatedSegments
                    .Where(s => !string.IsNullOrWhiteSpace(s.Text))
                    .ToDictionary(s => s.Id, s => s.Text);

                // Source-language transcript map: TranslatedSegmentId → original TranscriptSegment
                // text, correlated via SegmentIndex. Source audio speaks the original language, so
                // source forced-alignment must never receive the translated text above.
                Dictionary<int, string> transcriptTextByIndex = currentState.TranscriptSegments
                    .Where(ts => !string.IsNullOrWhiteSpace(ts.Text))
                    .ToDictionary(ts => ts.SegmentIndex, ts => ts.Text);

                Dictionary<Guid, string> sourceSegmentTranscriptMap = [];
                foreach (var translatedSeg in currentState.TranslatedSegments)
                {
                    if (transcriptTextByIndex.TryGetValue(translatedSeg.SegmentIndex, out string? sourceText))
                        sourceSegmentTranscriptMap[translatedSeg.Id] = sourceText;
                }

                // Build source-timing map: TranslatedSegmentId → source audio start/end seconds.
                // Uses SegmentIndex to correlate TranslatedSegment → TranscriptSegment.
                Dictionary<int, (double Start, double End)> transcriptTimingByIndex =
                    currentState.TranscriptSegments
                        .ToDictionary(
                            ts => ts.SegmentIndex,
                            ts => (ts.StartSeconds, ts.EndSeconds));

                Dictionary<Guid, SegmentSourceTiming> segmentSourceTimingMap = [];
                foreach (var translatedSeg in currentState.TranslatedSegments)
                {
                    if (transcriptTimingByIndex.TryGetValue(
                            translatedSeg.SegmentIndex,
                            out (double Start, double End) timing))
                    {
                        segmentSourceTimingMap[translatedSeg.Id] =
                            new SegmentSourceTiming(timing.Start, timing.End);
                    }
                }

                // Resolve absolute source audio path from the state's best-available audio.
                string? sourceAudioPath = string.IsNullOrWhiteSpace(currentState.AsrAudioRelativePath)
                    ? null
                    : artifactStore.GetPath(currentState.AsrAudioRelativePath);

                LipSyncStageRequest stageRequest = new(
                    ProjectId: currentState.ProjectState.Project.Id,
                    MediaAsset: currentState.ProjectState.MediaAsset,
                    TtsTakes: currentState.TtsTakes,
                    ExistingArtifacts: currentState.ProjectState.Artifacts,
                    IsEnabled: true,
                    PreferredModelAlias: request.PreferredModelAlias,
                    StretchBounds: request.StretchBounds,
                    SegmentTranscriptMap: segmentTranscriptMap,
                    SegmentSourceTimingMap: segmentSourceTimingMap.Count > 0 ? segmentSourceTimingMap : null,
                    SourceSegmentTranscriptMap: sourceSegmentTranscriptMap.Count > 0 ? sourceSegmentTranscriptMap : null,
                    SourceAudioPath: sourceAudioPath,
                    SourceLanguageCode: currentState.TranscriptLanguage,
                    TargetLanguageCode: currentState.SelectedTranslationTargetLanguage);

                await lipSyncStageHandler
                    .HandleAsync(stageRequest, token).ConfigureAwait(false);

                return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TranscriptProjectState> RunExclusiveAsync(
        Func<CancellationToken, Task<TranscriptProjectState>> operation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!operationGate.Wait(0))
            throw new InvalidOperationException(ConcurrentOperationMessage);

        try
        {
            return await operation(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}

public sealed record LipSyncAlignAllRequest(
    string? PreferredModelAlias = null,
    PhonemeStretchBounds? StretchBounds = null);
