using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class VoiceWorkflow(
    TranscriptProjectStateService stateService,
    VoiceAssignmentService voiceAssignmentService,
    TtsOrchestrationService ttsOrchestrationService)
{
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly VoiceAssignmentService voiceAssignmentService = voiceAssignmentService ?? throw new ArgumentNullException(nameof(voiceAssignmentService));
    private readonly TtsOrchestrationService ttsOrchestrationService = ttsOrchestrationService ?? throw new ArgumentNullException(nameof(ttsOrchestrationService));

    public async Task<TranscriptProjectState> AssignVoiceToSpeakerAsync(
        AssignVoiceToSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await voiceAssignmentService.AssignVoiceToSpeakerAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public Task<PreviewVoiceResult> PreviewVoiceAsync(
        PreviewVoiceRequest request,
        CancellationToken cancellationToken) =>
        ttsOrchestrationService.PreviewVoiceAsync(request, cancellationToken);

    public async Task<VoiceAssignment?> GetVoiceAssignmentHistoryAsync(
        Guid speakerId,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await voiceAssignmentService.GetVoiceAssignmentHistoryAsync(
            currentState.ProjectState.Project.Id,
            speakerId,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    private Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);
}
