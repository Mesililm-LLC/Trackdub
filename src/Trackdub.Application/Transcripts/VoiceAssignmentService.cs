using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class VoiceAssignmentService(
    IVoiceAssignmentRepository voiceAssignmentRepository,
    ITtsTakeRepository ttsTakeRepository,
    IVoiceCatalog voiceCatalog)
{
    private readonly IVoiceAssignmentRepository voiceAssignmentRepository = voiceAssignmentRepository ?? throw new ArgumentNullException(nameof(voiceAssignmentRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly IVoiceCatalog voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));

    public async Task AssignVoiceToSpeakerAsync(
        TranscriptProjectState currentState,
        AssignVoiceToSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        if (!currentState.Speakers.Any(speaker => speaker.Id == request.SpeakerId))
        {
            throw new InvalidOperationException("The selected speaker was not found.");
        }

        if (!voiceCatalog.TryGetVoice(request.VoiceId, out VoiceCatalogEntry? voice))
        {
            throw new InvalidOperationException($"Voicepack '{request.VoiceId}' is not available.");
        }

        VoiceAssignment? existing = await voiceAssignmentRepository.GetAsync(
            currentState.ProjectState.Project.Id,
            request.SpeakerId,
            cancellationToken).ConfigureAwait(false);
        string voiceModelId = string.IsNullOrWhiteSpace(request.VoiceModelId)
            ? "kokoro-onnx"
            : request.VoiceModelId.Trim();
        VoiceAssignment assignment = existing is null
            ? VoiceAssignment.Create(
                currentState.ProjectState.Project.Id,
                request.SpeakerId,
                voiceModelId,
                voice.VoiceId)
            : existing.AssignVoice(voiceModelId, voice.VoiceId, referenceClipArtifactId: existing.ReferenceClipArtifactId);
        bool routingChanged = existing is not null &&
                              (!string.Equals(existing.VoiceModelId, assignment.VoiceModelId, StringComparison.Ordinal) ||
                               !string.Equals(existing.VoiceVariant, assignment.VoiceVariant, StringComparison.Ordinal) ||
                               existing.ReferenceClipArtifactId != assignment.ReferenceClipArtifactId);

        await voiceAssignmentRepository.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
        if (routingChanged)
        {
            await ttsTakeRepository.MarkByVoiceAssignmentStaleAsync(
                currentState.ProjectState.Project.Id,
                assignment.Id,
                cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<VoiceAssignment?> GetVoiceAssignmentHistoryAsync(
        Guid projectId,
        Guid speakerId,
        CancellationToken cancellationToken) =>
        voiceAssignmentRepository.GetForSpeakerIncludingMergedAsync(projectId, speakerId, cancellationToken);

    public async Task RestoreVoiceAssignmentsAsync(
        Guid projectId,
        IReadOnlyList<VoiceAssignment> targetAssignments,
        CancellationToken cancellationToken)
    {
        VoiceAssignment[] currentAssignments = [.. await voiceAssignmentRepository.GetAllAsync(projectId, cancellationToken).ConfigureAwait(false)];
        Dictionary<Guid, VoiceAssignment> targetBySpeakerId = targetAssignments
            .Where(assignment => !assignment.IsFallback)
            .GroupBy(assignment => assignment.SpeakerId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(assignment => assignment.CreatedAtUtc).First());

        foreach (VoiceAssignment currentAssignment in currentAssignments)
        {
            if (!targetBySpeakerId.ContainsKey(currentAssignment.SpeakerId))
            {
                await voiceAssignmentRepository.DeleteAsync(currentAssignment.Id, cancellationToken).ConfigureAwait(false);
            }
        }

        foreach (VoiceAssignment targetAssignment in targetBySpeakerId.Values)
        {
            if (!voiceCatalog.TryGetVoice(
                    string.IsNullOrWhiteSpace(targetAssignment.VoiceVariant)
                        ? targetAssignment.VoiceModelId
                        : targetAssignment.VoiceVariant,
                    out _))
            {
                continue;
            }

            await voiceAssignmentRepository.SaveAsync(targetAssignment with
            {
                ProjectId = projectId,
                IsFallback = false
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<VoiceAssignmentWarning> BuildWarnings(
        IReadOnlyList<VoiceAssignment> voiceAssignments,
        IReadOnlyList<VoiceCatalogEntry> availableVoices,
        string? selectedTranslationTargetLanguage)
    {
        if (string.IsNullOrWhiteSpace(selectedTranslationTargetLanguage))
        {
            return [];
        }

        Dictionary<string, VoiceCatalogEntry> voicesById = availableVoices.ToDictionary(
            voice => voice.VoiceId,
            StringComparer.OrdinalIgnoreCase);
        string targetLanguage = selectedTranslationTargetLanguage.Trim().ToLowerInvariant();
        var warnings = new List<VoiceAssignmentWarning>();
        foreach (VoiceAssignment assignment in voiceAssignments)
        {
            string voiceId = string.IsNullOrWhiteSpace(assignment.VoiceVariant)
                ? assignment.VoiceModelId
                : assignment.VoiceVariant;
            if (!voicesById.TryGetValue(voiceId, out VoiceCatalogEntry? voice))
            {
                continue;
            }

            string voiceLanguage = NormalizeVoiceLanguageForComparison(voice.LanguageCode);
            if (!(Qwen3TtsDefaults.IsPresetVoiceId(voice.VoiceId) && Qwen3TtsDefaults.SupportsLanguage(targetLanguage)) && !string.Equals(voiceLanguage, targetLanguage, StringComparison.Ordinal))
            {
                warnings.Add(new VoiceAssignmentWarning(
                    assignment.SpeakerId,
                    voice.VoiceId,
                    $"Voicepack language {voice.LanguageCode} does not match target {targetLanguage}."));
            }
        }

        return warnings;
    }

    private static string NormalizeVoiceLanguageForComparison(string languageCode)
    {
        string normalized = languageCode.Trim().ToLowerInvariant();
        int separatorIndex = normalized.IndexOfAny(['-', '_']);
        return separatorIndex <= 0 ? normalized : normalized[..separatorIndex];
    }
}
