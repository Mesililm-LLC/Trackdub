using Trackdub.Domain.Speakers;

namespace Trackdub.Application.Transcripts;

public sealed class SpeakerWorkflow(
    TranscriptProjectStateService stateService,
    SpeakerAssignmentService speakerAssignmentService)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly SpeakerAssignmentService speakerAssignmentService = speakerAssignmentService ?? throw new ArgumentNullException(nameof(speakerAssignmentService));

    public async Task<TranscriptProjectState> RenameSpeakerAsync(
        RenameSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.RenameSpeakerAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> MergeSpeakersAsync(
        MergeSpeakersRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.MergeSpeakersAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MergedSpeakerEntry>> ListMergedSpeakersAsync(
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await speakerAssignmentService.ListMergedSpeakersAsync(currentState, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> DeleteEmptySpeakerAsync(
        DeleteEmptySpeakerRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.DeleteEmptySpeakerAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> AssignSpeakerToSegmentAsync(
        AssignSpeakerToSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.AssignSpeakerToSegmentAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> AssignSpeakerToSegmentsAsync(
        AssignSpeakerToSegmentsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.AssignSpeakerToSegmentsAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> CreateSpeakerFromSegmentsAsync(
        CreateSpeakerFromSegmentsRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.CreateSpeakerFromSegmentsAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> SplitSpeakerTurnAsync(
        SplitSpeakerTurnRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.SplitSpeakerTurnAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> ExtractReferenceClipAsync(
        ExtractReferenceClipRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.ExtractReferenceClipAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> ImportReferenceClipAsync(
        ImportReferenceClipRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.ImportReferenceClipAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RerunDiarizationAsync(
        RerunDiarizationRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await speakerAssignmentService.RerunDiarizationAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}
