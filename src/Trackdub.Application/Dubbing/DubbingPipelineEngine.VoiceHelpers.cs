using Trackdub.Application.Pipeline;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Dubbing;

public sealed partial class DubbingPipelineEngine
{
    private static bool IsCustomVoiceSelection(RuntimeModelSelections selections) =>
        Qwen3TtsDefaults.IsCustomVoiceAlias(selections.TtsModelAlias);

    private static bool FreshProjectMayUsePreset(DubbingSessionOptions options) =>
        (options.VoiceAssignmentOverrides?.Values.Any(Qwen3TtsDefaults.IsPresetVoiceId) ?? false) ||
        (options.AutoAssignFallbackVoices && !options.UseVoiceCloning &&
         !StockTtsVoiceMatcher.SupportsKokoro(options.TargetLanguageCode) &&
         Qwen3TtsDefaults.SupportsLanguage(options.TargetLanguageCode));

    private static IEnumerable<string> EnumerateOverrides(UnattendedVoicePlan plan, HashSet<Guid> speakers) =>
        plan.ExplicitVoiceIds.Where(pair => speakers.Contains(pair.Key))
            .Where(pair => plan.CloneBySpeaker?.GetValueOrDefault(pair.Key) != true)
            .Select(pair => pair.Value).Where(voice => !string.IsNullOrWhiteSpace(voice))
            .Select(voice => voice.Trim());

    private static IEnumerable<string> EnumerateFallbacks(UnattendedVoicePlan plan, HashSet<Guid> speakers) =>
        (plan.FallbackVoiceIds ?? []).Where(pair => speakers.Contains(pair.Key))
            .Where(pair => !plan.ExplicitVoiceIds.ContainsKey(pair.Key))
            .Select(pair => pair.Value).Where(voice => !string.IsNullOrWhiteSpace(voice))
            .Select(voice => voice.Trim());

    private static IEnumerable<string> EnumerateAssignments(
        TranscriptProjectState state, UnattendedVoicePlan plan, HashSet<Guid> speakers, string? language) =>
        state.VoiceAssignments.Where(assignment => IsEligibleAssignment(assignment, plan, speakers))
            .Select(assignment => ResolveAssignmentVoice(assignment, language))
            .Where(voice => voice is not null).Select(voice => voice!);

    private static bool IsEligibleAssignment(VoiceAssignment assignment, UnattendedVoicePlan plan, HashSet<Guid> speakers) =>
        !assignment.IsFallback && speakers.Contains(assignment.SpeakerId) &&
        !plan.ExplicitVoiceIds.ContainsKey(assignment.SpeakerId) &&
        plan.CloneBySpeaker?.GetValueOrDefault(assignment.SpeakerId) != true &&
        plan.FallbackVoiceIds?.ContainsKey(assignment.SpeakerId) != true;

    private static string? ResolveAssignmentVoice(VoiceAssignment assignment, string? language)
    {
        if (!string.IsNullOrWhiteSpace(assignment.VoiceVariant))
            return assignment.VoiceVariant.Trim();
        if (!VoiceCloningDefaults.IsCloneOnlyModelAlias(assignment.VoiceModelId))
            return null;
        if (StockTtsVoiceMatcher.SupportsKokoro(language))
            return StockTtsDefaults.KokoroPrimaryAlias;
        return Qwen3TtsDefaults.SupportsLanguage(language)
            ? Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(language) : null;
    }
}
