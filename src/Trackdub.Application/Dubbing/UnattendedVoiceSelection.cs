using System.Globalization;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Voice decisions for unattended (CLI/SDK) runs, which have no interactive voice-assignment
/// step: explicit <c>--voice</c> overrides, stock fallbacks, per-speaker cloning, and which TTS
/// model preflight must provision for the voices that will actually speak.
/// </summary>
internal static class UnattendedVoiceSelection
{
    internal static bool RequestsVoiceCloning(DubbingSessionOptions options) =>
        options.UseVoiceCloning ||
        (options.VoiceCloneBySpeakerId?.Values.Any(static clone => clone) ?? false);

    /// <summary>
    /// Unattended runs have no voice-assignment step, so speakers without a deliberate
    /// (non-fallback) voice assignment would fail TTS outright. Mirrors the shell's fallback
    /// behavior: picks the first catalog voice whose language matches the dub target language,
    /// ordered by display name like the shell's voice picker. Languages Kokoro does not cover use
    /// the Qwen3 CustomVoice default preset when Qwen3 speaks the language. Speakers stay
    /// unassigned otherwise, so TTS fails with the explicit assignment error instead of dubbing
    /// with a wrong-language voice.
    /// </summary>
    internal static Dictionary<Guid, string>? BuildUnattendedFallbackVoiceIds(
        TranscriptProjectState state,
        string? targetLanguageCode)
    {
        if (state.Speakers.Count == 0)
        {
            return null;
        }

        HashSet<Guid> deliberatelyAssignedSpeakerIds = state.VoiceAssignments
            .Where(static assignment => !assignment.IsFallback)
            .Select(static assignment => assignment.SpeakerId)
            .ToHashSet();

        // Languages Kokoro does not cover use the Qwen3 CustomVoice preset whose native language
        // matches. The picker list can contain every preset for these languages, so take the
        // language default instead of the alphabetically first preset (Aiden is English).
        // Otherwise pick the first matching stock voice.
        string? defaultVoiceId = !string.IsNullOrWhiteSpace(targetLanguageCode) &&
                                 !StockTtsVoiceMatcher.SupportsKokoro(targetLanguageCode)
            ? (Qwen3TtsDefaults.SupportsLanguage(targetLanguageCode)
                ? Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(targetLanguageCode)
                : null)
            : state.AvailableVoices
                .Where(voice => IsVoiceLanguageMatch(voice.LanguageCode, targetLanguageCode))
                .OrderBy(static voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(static voice => voice.VoiceId)
                .FirstOrDefault();
        if (defaultVoiceId is null)
        {
            return null;
        }

        Dictionary<Guid, string> fallbackVoiceIds = state.Speakers
            .Where(speaker => !deliberatelyAssignedSpeakerIds.Contains(speaker.Id))
            .ToDictionary(static speaker => speaker.Id, _ => defaultVoiceId);
        return fallbackVoiceIds.Count > 0 ? fallbackVoiceIds : null;
    }

    /// <summary>
    /// Pins Chatterbox (or an explicit TTS override) when headless voice cloning is requested.
    /// </summary>
    internal static DubbingSessionOptions ApplyVoiceCloningDefaults(DubbingSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!RequestsVoiceCloning(options))
        {
            return options;
        }

        Dictionary<string, string> preferences = CopyCaseInsensitive(options.ModelPreferences);

        // Preserve the explicit TTS override only when it names a clone-capable model.
        // A stock alias (e.g. kokoro-onnx) paired with UseVoiceCloning=true would otherwise
        // reach an incompatible voicepack lookup that throws "Voicepack '...' is not available.";
        // pin the default Chatterbox clone model instead so the clone run stays runnable.
        // Return the case-insensitive copy so BuildModelPreferences' GetValueOrDefault
        // lookup resolves regardless of the casing the caller used for the original TTS key.
        bool keepsExplicitCloneModel =
            preferences.TryGetValue(StageNames.Tts, out string? explicitTtsAlias) &&
            VoiceCloningDefaults.IsCloneOnlyModelAlias(explicitTtsAlias);
        if (!keepsExplicitCloneModel)
        {
            preferences[StageNames.Tts] = VoiceCloningDefaults.ResolveDefaultCloneModelAlias(options.TargetLanguageCode);
        }

        return options with { ModelPreferences = preferences };
    }

    /// <summary>
    /// Callers may hand us an arbitrary IReadOnlyDictionary whose comparer is case-sensitive
    /// (e.g. StringComparer.Ordinal), so it can legitimately contain keys that differ only by
    /// case such as both "TTS" and "tts". Copying via the collection constructor with an
    /// OrdinalIgnoreCase comparer throws ArgumentException on those duplicates, so normalize
    /// by explicit enumeration. Duplicate-precedence: last write wins in the source's
    /// enumeration order (matches Dictionary enumeration, but that order is not guaranteed).
    /// </summary>
    private static Dictionary<string, string> CopyCaseInsensitive(IReadOnlyDictionary<string, string>? source)
    {
        var copy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, string> pair in source ?? new Dictionary<string, string>())
        {
            copy[pair.Key] = pair.Value;
        }

        return copy;
    }

    /// <summary>
    /// Builds the unattended TTS request: explicit <c>--voice</c> assignments, stock Kokoro
    /// fallbacks for remaining speakers, and per-speaker source-audio cloning when
    /// <see cref="DubbingSessionOptions.UseVoiceCloning"/> is set. Cloning is skipped for
    /// speakers that have a voice override.
    /// </summary>
    internal static GenerateTtsForAllSpeakersRequest BuildUnattendedTtsRequest(
        TranscriptProjectState state,
        DubbingSessionOptions options,
        string? ttsModelAlias)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        UnattendedVoicePlan voicePlan = ResolveUnattendedVoicePlan(state, options);
        IReadOnlyDictionary<Guid, string> explicitVoiceIds = voicePlan.ExplicitVoiceIds;
        Dictionary<Guid, bool>? cloneBySpeaker = voicePlan.CloneBySpeaker;
        Dictionary<Guid, string>? fallbackVoiceIds = voicePlan.FallbackVoiceIds;

        string? preferredModelAlias = ttsModelAlias;
        if (RequestsVoiceCloning(options) && string.IsNullOrWhiteSpace(preferredModelAlias))
        {
            preferredModelAlias = VoiceCloningDefaults.ResolveDefaultCloneModelAlias(options.TargetLanguageCode);
        }

        return new GenerateTtsForAllSpeakersRequest(
            FallbackVoiceIdsBySpeakerId: fallbackVoiceIds,
            PreferredModelAlias: preferredModelAlias,
            UseReferenceClipForVoiceCloningBySpeakerId: cloneBySpeaker,
            VoiceIdsBySpeakerId: explicitVoiceIds.Count > 0 ? explicitVoiceIds : null);
    }

    /// <summary>
    /// Maps CLI <c>--voice SPEAKER_ID:voice_id</c> keys onto project speakers.
    /// Accepts a speaker Guid, display name (Speaker 1), or Whisper-style <c>SPEAKER_00</c> index.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, string> ResolveVoiceAssignmentOverrides(
        TranscriptProjectState state,
        IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (overrides is null || overrides.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        ProjectSpeaker[] speakers =
        [
            .. state.Speakers
                .OrderBy(static speaker => speaker.CreatedAtUtc)
                .ThenBy(static speaker => speaker.Id)
        ];

        // Fresh projects have no speakers yet (diarization hasn't run), and pre-flight
        // resolves the voice plan against that empty state. Index- and name-based keys
        // cannot be validated until speakers exist, so defer matching: pre-flight
        // proceeds on fallback voices and the TTS stage re-resolves — rejecting
        // genuinely bad keys — once speakers are known.
        if (speakers.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var resolved = new Dictionary<Guid, string>();
        foreach ((string key, string voiceId) in overrides)
        {
            if (!TryMatchSpeaker(speakers, key, out ProjectSpeaker? speaker) || speaker is null)
            {
                string known = speakers.Length == 0
                    ? "(none)"
                    : string.Join(", ", speakers.Select(static candidate => candidate.DisplayName));
                throw new InvalidOperationException(
                    $"Voice override '{key}' did not match a speaker. Known speakers: {known}. Use a display name (Speaker 1) or Whisper-style id (SPEAKER_00).");
            }

            resolved[speaker.Id] = voiceId;
        }

        return resolved;
    }

    internal static bool TryMatchSpeaker(
        IReadOnlyList<ProjectSpeaker> speakers,
        string key,
        out ProjectSpeaker? speaker)
    {
        speaker = null;
        if (string.IsNullOrWhiteSpace(key) || speakers.Count == 0)
        {
            return false;
        }

        string trimmed = key.Trim();
        speaker = Guid.TryParse(trimmed, out Guid speakerId)
            ? speakers.FirstOrDefault(candidate => candidate.Id == speakerId)
            : speakers.FirstOrDefault(candidate => candidate.DisplayName.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
              ?? FindByWhisperIndex(speakers, trimmed);
        return speaker is not null;
    }

    /// <summary>Resolves a Whisper-style <c>SPEAKER_00</c> key to the speaker at that index.</summary>
    private static ProjectSpeaker? FindByWhisperIndex(IReadOnlyList<ProjectSpeaker> speakers, string key)
    {
        const string whisperPrefix = "speaker_";
        if (!key.StartsWith(whisperPrefix, StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(key[whisperPrefix.Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
        {
            return null;
        }

        return index >= 0 && index < speakers.Count ? speakers[index] : null;
    }

    private static bool IsVoiceLanguageMatch(string voiceLanguageCode, string? targetLanguageCode)
    {
        string? normalizedVoiceLanguage = NormalizeVoiceLanguageCode(voiceLanguageCode);
        string? normalizedTargetLanguage = NormalizeVoiceLanguageCode(targetLanguageCode);
        if (normalizedVoiceLanguage is null || normalizedTargetLanguage is null)
        {
            return true;
        }

        return string.Equals(normalizedVoiceLanguage, normalizedTargetLanguage, StringComparison.Ordinal) ||
               normalizedVoiceLanguage.StartsWith($"{normalizedTargetLanguage}-", StringComparison.Ordinal) ||
               normalizedTargetLanguage.StartsWith($"{normalizedVoiceLanguage}-", StringComparison.Ordinal);
    }

    private static string? NormalizeVoiceLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().Replace('_', '-').ToLowerInvariant();

    /// <summary>
    /// When every voice this run will synthesize is a Qwen3 preset, point preflight at CustomVoice.
    /// Clone runs are left unchanged, because that single alias is the clone model. An alias that
    /// is already CustomVoice is left unchanged, including a larger tier. A mix of preset and stock
    /// voices keeps the stock alias and is provisioned alongside CustomVoice by
    /// <see cref="RequiresCompanionCustomVoiceModel"/>.
    /// </summary>
    internal static RuntimeModelSelections ApplyPresetVoiceModelSelection(
        RuntimeModelSelections selections,
        DubbingSessionOptions options,
        TranscriptProjectState? state)
    {
        List<string>? allVoiceIds = ResolveStockVoiceIdsForPresetCheck(selections, options, state);
        return allVoiceIds is { Count: > 0 } && allVoiceIds.All(Qwen3TtsDefaults.IsPresetVoiceId)
            ? selections with
            {
                TtsModelAlias = Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null),
                SelectionIntents = Enum.GetValues<RuntimeStage>().ToDictionary(stage => stage, selections.GetSelectionIntent)
            }
            : selections;
    }

    /// <summary>
    /// True when synthesis will require CustomVoice for at least one speaker while the selected
    /// TTS alias stays a different model. Preflight must provision both.
    /// </summary>
    internal static bool RequiresCompanionCustomVoiceModel(
        RuntimeModelSelections selections,
        DubbingSessionOptions options,
        TranscriptProjectState? state)
    {
        List<string>? voiceIds = ResolveStockVoiceIdsForPresetCheck(selections, options, state);
        return voiceIds is not null && MixesPresetAndStockVoices(voiceIds);
    }

    private static bool MixesPresetAndStockVoices(List<string> voiceIds) =>
        voiceIds.Any(Qwen3TtsDefaults.IsPresetVoiceId) &&
        voiceIds.Any(static voiceId => !Qwen3TtsDefaults.IsPresetVoiceId(voiceId));

    /// <summary>
    /// Voice ids this run will synthesize without cloning, or null when the preset checks do not
    /// apply: no project state yet, a clone run, or a TTS alias that is already CustomVoice.
    /// </summary>
    private static List<string>? ResolveStockVoiceIdsForPresetCheck(
        RuntimeModelSelections selections,
        DubbingSessionOptions options,
        TranscriptProjectState? state)
    {
        if (state is null)
        {
            return null;
        }

        if (RequestsVoiceCloning(options) || Qwen3TtsDefaults.IsCustomVoiceAlias(selections.TtsModelAlias))
        {
            return null;
        }

        return EnumerateUnattendedStockVoiceIds(
            state,
            ResolveUnattendedVoicePlan(state, options),
            options.TargetLanguageCode).ToList();
    }

    private readonly record struct UnattendedVoicePlan(
        IReadOnlyDictionary<Guid, string> ExplicitVoiceIds,
        Dictionary<Guid, string>? FallbackVoiceIds,
        Dictionary<Guid, bool>? CloneBySpeaker);

    /// <summary>
    /// Speakers this unattended run will synthesize without cloning, and the voice id each one uses.
    /// Shared by the TTS request and preset preflight so fallback and clone filters cannot drift.
    /// </summary>
    private static UnattendedVoicePlan ResolveUnattendedVoicePlan(
        TranscriptProjectState state,
        DubbingSessionOptions options)
    {
        IReadOnlyDictionary<Guid, string> explicitVoiceIds = ResolveVoiceAssignmentOverrides(
            state,
            options.VoiceAssignmentOverrides);

        Dictionary<Guid, bool>? cloneBySpeaker = null;
        if (options.VoiceCloneBySpeakerId is not null)
        {
            cloneBySpeaker = new Dictionary<Guid, bool>(options.VoiceCloneBySpeakerId);
        }
        else if (options.UseVoiceCloning && state.Speakers.Count > 0)
        {
            cloneBySpeaker = state.Speakers.ToDictionary(
                static speaker => speaker.Id,
                speaker => !explicitVoiceIds.ContainsKey(speaker.Id));
        }

        Dictionary<Guid, string>? fallbackVoiceIds = null;
        if (options.AutoAssignFallbackVoices)
        {
            fallbackVoiceIds = BuildUnattendedFallbackVoiceIds(state, options.TargetLanguageCode);
            if (fallbackVoiceIds is not null)
            {
                fallbackVoiceIds = fallbackVoiceIds
                    .Where(pair => !explicitVoiceIds.ContainsKey(pair.Key))
                    .Where(pair => cloneBySpeaker?.GetValueOrDefault(pair.Key) != true)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                if (fallbackVoiceIds.Count == 0)
                {
                    fallbackVoiceIds = null;
                }
            }
        }

        return new UnattendedVoicePlan(explicitVoiceIds, fallbackVoiceIds, cloneBySpeaker);
    }

    private static IEnumerable<string> EnumerateUnattendedStockVoiceIds(
        TranscriptProjectState state,
        UnattendedVoicePlan plan,
        string? targetLanguage)
    {
        // GenerateTtsForAllSpeakersAsync skips speakers with no transcript segments, then
        // prefers a request override, then a persisted non-fallback assignment, then the fallback.
        HashSet<Guid> synthesizingSpeakerIds = state.TranscriptSegments
            .Select(static segment => segment.SpeakerId)
            .Where(static speakerId => speakerId is not null)
            .Select(static speakerId => speakerId!.Value)
            .ToHashSet();

        IEnumerable<string> explicitVoiceIds = SelectSpeakerVoiceIds(
            plan.ExplicitVoiceIds,
            synthesizingSpeakerIds.Contains);
        IEnumerable<string> fallbackVoiceIds = SelectSpeakerVoiceIds(
            plan.FallbackVoiceIds,
            speakerId => synthesizingSpeakerIds.Contains(speakerId) && !plan.ExplicitVoiceIds.ContainsKey(speakerId));
        IEnumerable<string> persistedVoiceIds = state.VoiceAssignments
            .Where(assignment => synthesizingSpeakerIds.Contains(assignment.SpeakerId) &&
                                 !IsSupersededByUnattendedPlan(assignment, plan))
            .Select(assignment => ResolvePersistedStockVoiceId(assignment, targetLanguage))
            .OfType<string>();

        return explicitVoiceIds.Concat(fallbackVoiceIds).Concat(persistedVoiceIds);
    }

    private static IEnumerable<string> SelectSpeakerVoiceIds(
        IReadOnlyDictionary<Guid, string>? voiceIdsBySpeaker,
        Func<Guid, bool> includeSpeaker) =>
        voiceIdsBySpeaker is null
            ? Enumerable.Empty<string>()
            : voiceIdsBySpeaker
                .Where(pair => includeSpeaker(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
                .Select(static pair => pair.Value.Trim());

    private static bool IsSupersededByUnattendedPlan(VoiceAssignment assignment, UnattendedVoicePlan plan) =>
        assignment.IsFallback ||
        plan.ExplicitVoiceIds.ContainsKey(assignment.SpeakerId) ||
        plan.CloneBySpeaker?.GetValueOrDefault(assignment.SpeakerId) == true ||
        plan.FallbackVoiceIds?.ContainsKey(assignment.SpeakerId) == true;

    private static string? ResolvePersistedStockVoiceId(VoiceAssignment assignment, string? targetLanguage)
    {
        if (!string.IsNullOrWhiteSpace(assignment.VoiceVariant))
        {
            return assignment.VoiceVariant.Trim();
        }

        // A prior clone run persists a clone-only model with no voice id. Synthesis
        // substitutes Kokoro where Kokoro speaks, and a Qwen preset where Qwen speaks.
        // Languages neither speaks stay unassigned, matching BuildUnattendedFallbackVoiceIds.
        // KokoroPrimaryAlias is a model alias, not a catalog voice id; it only has to fail
        // IsPresetVoiceId so this speaker is not counted as a Qwen preset.
        if (!VoiceCloningDefaults.IsCloneOnlyModelAlias(assignment.VoiceModelId))
        {
            return null;
        }

        if (StockTtsVoiceMatcher.SupportsKokoro(targetLanguage))
        {
            return StockTtsDefaults.KokoroPrimaryAlias;
        }

        return Qwen3TtsDefaults.SupportsLanguage(targetLanguage)
            ? Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(targetLanguage)
            : null;
    }
}
