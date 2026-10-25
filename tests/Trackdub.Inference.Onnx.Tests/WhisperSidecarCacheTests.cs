using System.Text.Json;
using Trackdub.Inference.Onnx.Whisper;

namespace Trackdub.Inference.Onnx.Tests;

/// <summary>
/// The mel-bin count was re-parsed from <c>preprocessor_config.json</c> on every transcription and
/// every TensorRT profile build. These tests pin that it is read once per model root and that the
/// cache is invalidatable, since a stale value would silently change mel shapes.
/// </summary>
public sealed class WhisperSidecarCacheTests
{
    [Theory]
    [InlineData(80)]
    [InlineData(128)]
    public void ReadMelBins_returns_the_configured_feature_size(int featureSize)
    {
        string root = CreateModelRoot(featureSize);
        try
        {
            Assert.Equal(featureSize, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(root));
        }
        finally
        {
            WhisperSidecarCaches.Remove(root);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ReadMelBins_falls_back_to_the_default_when_the_config_is_absent()
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-whisper-mel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Equal(80, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(root));
        }
        finally
        {
            WhisperSidecarCaches.Remove(root);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void ReadMelBins_is_cached_per_model_root_and_reflects_an_invalidated_change()
    {
        string root = CreateModelRoot(80);
        try
        {
            Assert.Equal(80, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(root));

            // Rewrite the file underneath the cache: the cached value must still be served,
            // which is what proves the read happened once rather than per call.
            WriteConfig(root, 128);
            Assert.Equal(80, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(root));

            // Explicit invalidation is the documented way to observe a changed model root.
            WhisperSidecarCaches.Remove(root);
            Assert.Equal(128, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(root));
        }
        finally
        {
            WhisperSidecarCaches.Remove(root);
            DeleteDirectory(root);
        }
    }

    [Fact]
    public void Distinct_model_roots_do_not_share_mel_bins()
    {
        string first = CreateModelRoot(80);
        string second = CreateModelRoot(128);
        try
        {
            Assert.Equal(80, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(first));
            Assert.Equal(128, WhisperOnnxAudioTranscriptionEngine.ReadMelBins(second));
        }
        finally
        {
            WhisperSidecarCaches.Remove(first);
            WhisperSidecarCaches.Remove(second);
            DeleteDirectory(first);
            DeleteDirectory(second);
        }
    }

    private static string CreateModelRoot(int featureSize)
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-whisper-mel-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        WriteConfig(root, featureSize);
        return root;
    }

    private static void WriteConfig(string root, int featureSize) =>
        File.WriteAllText(
            Path.Join(root, "preprocessor_config.json"),
            JsonSerializer.Serialize(new { feature_size = featureSize }));

    private static void DeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }
}
