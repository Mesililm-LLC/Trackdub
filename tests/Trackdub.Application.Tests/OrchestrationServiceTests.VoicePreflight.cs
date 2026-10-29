using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Tests;

public sealed partial class OrchestrationServiceTests
{
    [Theory]
    [InlineData("zh", true)]
    [InlineData("es", false)]
    public void Fresh_project_provisions_preset_fallback_as_companion(string language, bool expected)
    {
        TranscriptProjectState state = CreateTranscriptProjectState() with
        {
            Speakers = [], TranscriptSegments = [], VoiceAssignments = [],
        };
        var options = new Trackdub.Contracts.Dubbing.DubbingSessionOptions
        {
            SourceMediaPath = "source.mp4", TargetLanguageCode = language, AutoAssignFallbackVoices = true,
        };
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto, false, new Dictionary<string, ExecutionProviderKind>(), TtsModelAlias: "kokoro-onnx");

        Assert.Equal(expected, Trackdub.Application.Dubbing.UnattendedVoiceSelection
            .RequiresCompanionCustomVoiceModel(selections, options, state));
    }

    [Fact]
    public void Clone_run_provisions_preset_for_non_cloned_speaker_and_keeps_clone_alias()
    {
        TranscriptProjectState state = CreateTwoSpeakerTranslatedProjectState();
        var options = new Trackdub.Contracts.Dubbing.DubbingSessionOptions
        {
            SourceMediaPath = "source.mp4", TargetLanguageCode = "zh",
            VoiceCloneBySpeakerId = new Dictionary<Guid, bool>
            {
                [state.Speakers[0].Id] = true, [state.Speakers[1].Id] = false,
            },
            VoiceAssignmentOverrides = new Dictionary<string, string>
            {
                [state.Speakers[0].DisplayName] = Qwen3TtsDefaults.PresetVoicePrefix + "ryan",
                [state.Speakers[1].DisplayName] = Qwen3TtsDefaults.PresetVoicePrefix + "vivian",
            },
        };
        var selections = new RuntimeModelSelections(
            AsrModelOverride.Auto, false, new Dictionary<string, ExecutionProviderKind>(),
            TtsModelAlias: Qwen3TtsDefaults.Base06Alias);

        Assert.Equal(selections.TtsModelAlias, Trackdub.Application.Dubbing.UnattendedVoiceSelection
            .ApplyPresetVoiceModelSelection(selections, options, state).TtsModelAlias);
        Assert.True(Trackdub.Application.Dubbing.UnattendedVoiceSelection
            .RequiresCompanionCustomVoiceModel(selections, options, state));

        options = options with { VoiceCloneBySpeakerId = state.Speakers.ToDictionary(s => s.Id, _ => true) };
        Assert.False(Trackdub.Application.Dubbing.UnattendedVoiceSelection
            .RequiresCompanionCustomVoiceModel(selections, options, state));
    }
}
