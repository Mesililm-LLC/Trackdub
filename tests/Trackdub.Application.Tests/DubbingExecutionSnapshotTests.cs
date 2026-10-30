using Trackdub.Application.Dubbing;
using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Tests;

public sealed class DubbingExecutionSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CaptureExecutionSnapshot_records_speech_audio_preparation_choice(bool enabled)
    {
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = "source.mp4",
            TargetLanguageCode = "fr",
            EnableSpeechAudioPreparation = enabled,
        };

        Dictionary<string, string> snapshot = DubbingExecutionSnapshot.CaptureExecutionSnapshot(options);

        Assert.Equal(enabled.ToString(), snapshot["EnableSpeechAudioPreparation"]);
    }
}
