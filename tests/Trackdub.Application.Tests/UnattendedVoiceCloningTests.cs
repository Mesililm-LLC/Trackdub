using Trackdub.Application.Dubbing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;

namespace Trackdub.Application.Tests;

/// <summary>
/// Headless voice-clone wiring: pin Chatterbox when cloning is requested, and
/// emit per-speaker source-audio clone flags instead of stock Kokoro fallbacks.
/// </summary>
public sealed class UnattendedVoiceCloningTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    [Fact]
    public void ApplyVoiceCloningDefaults_WhenCloningEnglish_PinsChatterboxTurbo()
    {
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            ModelPreferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["asr"] = "whisper-small-genai",
            },
            UseVoiceCloning = true,
        };

        DubbingSessionOptions resolved = UnattendedVoiceSelection.ApplyVoiceCloningDefaults(options);

        Assert.NotNull(resolved.ModelPreferences);
        Assert.Equal(VoiceCloningDefaults.ChatterboxPrimaryAlias, resolved.ModelPreferences["tts"]);
        Assert.Equal("whisper-small-genai", resolved.ModelPreferences["asr"]);
    }

    [Fact]
    public void ApplyVoiceCloningDefaults_PreservesExplicitTtsOverride()
    {
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            ModelPreferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["tts"] = "chatterbox-multilingual",
            },
            UseVoiceCloning = true,
        };

        DubbingSessionOptions resolved = UnattendedVoiceSelection.ApplyVoiceCloningDefaults(options);

        Assert.NotNull(resolved.ModelPreferences);
        Assert.Equal("chatterbox-multilingual", resolved.ModelPreferences!["tts"]);
    }

    [Fact]
    public void ApplyVoiceCloningDefaults_WhenTtsKeyIsCaseSensitive_NormalizesToOrdinalIgnoreCase()
    {
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            ModelPreferences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TTS"] = "chatterbox-multilingual",
                ["asr"] = "whisper-small-genai",
            },
            UseVoiceCloning = true,
        };

        DubbingSessionOptions resolved = UnattendedVoiceSelection.ApplyVoiceCloningDefaults(options);

        Assert.NotNull(resolved.ModelPreferences);
        // The explicit override survives a case-insensitive lookup (StageNames.Tts == "tts").
        Assert.Equal("chatterbox-multilingual", resolved.ModelPreferences![StageNames.Tts]);
        Assert.Equal("chatterbox-multilingual", resolved.ModelPreferences["tts"]);
        // All original entries are preserved.
        Assert.Equal("whisper-small-genai", resolved.ModelPreferences["asr"]);
    }

    [Fact]
    public void ApplyVoiceCloningDefaults_WhenCaseSensitiveDictionaryHasDuplicateTtsKeys_DoesNotThrowAndNormalizes()
    {
        // A case-sensitive (Ordinal) dictionary can legitimately hold two keys that differ only by
        // case. Normalizing to OrdinalIgnoreCase must not throw ArgumentException on the duplicate;
        // last write wins in enumeration order.
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            ModelPreferences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TTS"] = "chatterbox-turbo",
                ["tts"] = "chatterbox-multilingual",
            },
            UseVoiceCloning = true,
        };

        DubbingSessionOptions resolved = UnattendedVoiceSelection.ApplyVoiceCloningDefaults(options);

        Assert.NotNull(resolved.ModelPreferences);
        // Last write wins: the "tts" entry, enumerated after "TTS", is retained.
        // Normalization did not throw; a single case-insensitive TTS entry exists.
        Assert.True(resolved.ModelPreferences!.ContainsKey(StageNames.Tts));
    }

    [Fact]
    public void BuildModelPreferences_WhenTtsKeyIsCaseSensitive_RuntimeSelectionRetainsAlias()
    {
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            ModelPreferences = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TTS"] = "chatterbox-multilingual",
            },
            UseVoiceCloning = true,
        };

        // Drive the real runtime-selection path: normalize, then feed the result through the same
        // BuildModelPreferences lookup used by CreateSelectionsFromSettings so a regression that
        // reverts to reading the caller's case-sensitive dictionary is caught here.
        DubbingSessionOptions resolved = UnattendedVoiceSelection.ApplyVoiceCloningDefaults(options);
        InferenceModelPreferences? preferences = DubbingModelPreferences.BuildModelPreferences(resolved);

        Assert.NotNull(preferences);
        Assert.Equal("chatterbox-multilingual", preferences!.TtsModelAlias);
    }

    [Fact]
    public void ApplyVoiceCloningDefaults_WhenNotCloning_LeavesOptionsUnchanged()
    {
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
        };

        Assert.Same(options, UnattendedVoiceSelection.ApplyVoiceCloningDefaults(options));
    }

    [Fact]
    public void BuildUnattendedTtsRequest_WhenCloning_EnablesPerSpeakerReferenceClips()
    {
        ProjectSpeaker speakerA = CreateSpeaker("Speaker 1");
        ProjectSpeaker speakerB = CreateSpeaker("Speaker 2");
        TranscriptProjectState state = BuildState([speakerA, speakerB]);

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            UseVoiceCloning = true,
        };

        GenerateTtsForAllSpeakersRequest request = UnattendedVoiceSelection.BuildUnattendedTtsRequest(
            state,
            options,
            ttsModelAlias: null);

        Assert.Null(request.FallbackVoiceIdsBySpeakerId);
        Assert.Equal(VoiceCloningDefaults.ChatterboxPrimaryAlias, request.PreferredModelAlias);
        Assert.NotNull(request.UseReferenceClipForVoiceCloningBySpeakerId);
        Assert.Equal(2, request.UseReferenceClipForVoiceCloningBySpeakerId.Count);
        Assert.True(request.UseReferenceClipForVoiceCloningBySpeakerId[speakerA.Id]);
        Assert.True(request.UseReferenceClipForVoiceCloningBySpeakerId[speakerB.Id]);
    }

    [Fact]
    public void BuildUnattendedTtsRequest_WhenCloning_KeepsExplicitTtsAlias()
    {
        TranscriptProjectState state = BuildState([CreateSpeaker("Speaker 1")]);
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            UseVoiceCloning = true,
        };

        GenerateTtsForAllSpeakersRequest request = UnattendedVoiceSelection.BuildUnattendedTtsRequest(
            state,
            options,
            ttsModelAlias: "chatterbox-turbo");

        Assert.Equal("chatterbox-turbo", request.PreferredModelAlias);
        Assert.Null(request.FallbackVoiceIdsBySpeakerId);
    }

    [Fact]
    public void BuildUnattendedTtsRequest_WhenNotCloning_AssignsStockFallbackVoices()
    {
        ProjectSpeaker speaker = CreateSpeaker("Speaker 1");
        TranscriptProjectState state = BuildState(
            [speaker],
            availableVoices: [new VoiceCatalogEntry("am_adam", "en-us", "male", "Adam")]);

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
        };

        GenerateTtsForAllSpeakersRequest request = UnattendedVoiceSelection.BuildUnattendedTtsRequest(
            state,
            options,
            ttsModelAlias: "kokoro-onnx");

        Assert.Null(request.UseReferenceClipForVoiceCloningBySpeakerId);
        Assert.Equal("kokoro-onnx", request.PreferredModelAlias);
        Assert.NotNull(request.FallbackVoiceIdsBySpeakerId);
        Assert.Equal("am_adam", request.FallbackVoiceIdsBySpeakerId[speaker.Id]);
    }

    [Fact]
    public void BuildUnattendedTtsRequest_WhenNotCloning_AppliesVoiceOverrides()
    {
        DateTimeOffset createdAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ProjectSpeaker speakerA = CreateSpeaker("Speaker 1", createdAt);
        ProjectSpeaker speakerB = CreateSpeaker("Speaker 2", createdAt.AddSeconds(1));
        TranscriptProjectState state = BuildState(
            [speakerA, speakerB],
            availableVoices: [new VoiceCatalogEntry("am_adam", "en-us", "male", "Adam")]);

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            VoiceAssignmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SPEAKER_00"] = "af_bella",
            },
        };

        GenerateTtsForAllSpeakersRequest request = UnattendedVoiceSelection.BuildUnattendedTtsRequest(
            state,
            options,
            ttsModelAlias: "kokoro-onnx");

        Assert.Null(request.UseReferenceClipForVoiceCloningBySpeakerId);
        Assert.NotNull(request.VoiceIdsBySpeakerId);
        Assert.Equal("af_bella", request.VoiceIdsBySpeakerId[speakerA.Id]);
        Assert.False(request.VoiceIdsBySpeakerId.ContainsKey(speakerB.Id));
        Assert.NotNull(request.FallbackVoiceIdsBySpeakerId);
        Assert.Equal("am_adam", request.FallbackVoiceIdsBySpeakerId[speakerB.Id]);
        Assert.False(request.FallbackVoiceIdsBySpeakerId.ContainsKey(speakerA.Id));
    }

    [Fact]
    public void BuildUnattendedTtsRequest_WhenCloning_SkipsCloneForVoiceOverrides()
    {
        DateTimeOffset createdAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        ProjectSpeaker speakerA = CreateSpeaker("Speaker 1", createdAt);
        ProjectSpeaker speakerB = CreateSpeaker("Speaker 2", createdAt.AddSeconds(1));
        TranscriptProjectState state = BuildState([speakerA, speakerB]);

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            UseVoiceCloning = true,
            VoiceAssignmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Speaker 1"] = "af_bella",
            },
        };

        GenerateTtsForAllSpeakersRequest request = UnattendedVoiceSelection.BuildUnattendedTtsRequest(
            state,
            options,
            ttsModelAlias: null);

        Assert.Equal(VoiceCloningDefaults.ChatterboxPrimaryAlias, request.PreferredModelAlias);
        Assert.Null(request.FallbackVoiceIdsBySpeakerId);
        Assert.NotNull(request.VoiceIdsBySpeakerId);
        Assert.Equal("af_bella", request.VoiceIdsBySpeakerId[speakerA.Id]);
        Assert.NotNull(request.UseReferenceClipForVoiceCloningBySpeakerId);
        Assert.False(request.UseReferenceClipForVoiceCloningBySpeakerId[speakerA.Id]);
        Assert.True(request.UseReferenceClipForVoiceCloningBySpeakerId[speakerB.Id]);
    }

    [Fact]
    public void BuildUnattendedTtsRequest_WhenVoiceOverrideDoesNotMatch_Throws()
    {
        TranscriptProjectState state = BuildState([CreateSpeaker("Speaker 1")]);
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "clip.mp4",
            TargetLanguageCode = "en",
            VoiceAssignmentOverrides = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["SPEAKER_09"] = "af_bella",
            },
        };

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            UnattendedVoiceSelection.BuildUnattendedTtsRequest(state, options, ttsModelAlias: "kokoro-onnx"));
        Assert.Contains("SPEAKER_09", ex.Message, StringComparison.Ordinal);
    }

    private static ProjectSpeaker CreateSpeaker(string displayName) =>
        CreateSpeaker(displayName, DateTimeOffset.UtcNow);

    private static ProjectSpeaker CreateSpeaker(string displayName, DateTimeOffset createdAtUtc) =>
        new(Guid.NewGuid(), ProjectId, displayName, createdAtUtc);

    private static TranscriptProjectState BuildState(
        IReadOnlyList<ProjectSpeaker> speakers,
        IReadOnlyList<VoiceCatalogEntry>? availableVoices = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(ProjectId, "Test Project", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            ProjectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            4.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var projectState = new OpenProjectResult(
            project,
            mediaAsset,
            null,
            SourceMediaStatus.Available,
            null,
            [],
            "en");
        TranscriptRevision transcriptRevision = TranscriptRevision.Create(
            ProjectId,
            stageRunId: null,
            revisionNumber: 1,
            now);

        return new TranscriptProjectState(
            projectState,
            transcriptRevision,
            [],
            speakers,
            [],
            null,
            [],
            IsTranslationStale: false,
            TranscriptLanguage: "en",
            StageRuns: [],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: "en",
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: availableVoices ?? [],
            VoiceAssignments: [],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }
}
