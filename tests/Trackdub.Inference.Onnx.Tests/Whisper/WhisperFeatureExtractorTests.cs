using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.Whisper;

namespace Trackdub.Inference.Onnx.Tests.Whisper;

public sealed class WhisperFeatureExtractorTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(16000, 102)] // 1 s
    [InlineData(48000, 302)] // 3 s
    [InlineData(480000, 3000)] // full 30 s window
    [InlineData(960000, 3000)] // over-long input is trimmed to the 30 s window
    public void ComputeFrameCount_sizes_stft_to_actual_length(int sampleCount, int expectedFrames)
    {
        Assert.Equal(expectedFrames, WhisperFeatureExtractor.ComputeFrameCount(sampleCount));
    }

    [Fact]
    public void Extract_three_second_chunk_computes_only_actual_frames_but_keeps_encoder_shape()
    {
        var extractor = new WhisperFeatureExtractor();
        float[] samples = MakeSamples(48000);

        DenseTensor<float> features = extractor.Extract(samples);

        // Sanity bound for a 3 s region: STFT/mel run over ~302 frames, not 3000.
        // Asserted as a frame count, never as wall-clock timing.
        Assert.Equal(302, extractor.LastComputedFrameCount);
        Assert.Equal([1, 80, 3000], features.Dimensions.ToArray());
    }

    [Theory]
    [InlineData(100)]
    [InlineData(16000)]
    [InlineData(48000)]
    [InlineData(123456)]
    public void Extract_short_chunk_matches_frames_of_full_thirty_second_computation(int sampleCount)
    {
        float[] samples = MakeSamples(sampleCount);
        var extractor = new WhisperFeatureExtractor();

        DenseTensor<float> shortFeatures = extractor.Extract(samples);

        // The pre-M1 path: the same samples zero-padded to the full 30 s window.
        float[] fullWindow = new float[480000];
        samples.CopyTo(fullWindow, 0);
        DenseTensor<float> fullFeatures = new WhisperFeatureExtractor().Extract(fullWindow);

        Assert.Equal(fullFeatures.Dimensions.ToArray(), shortFeatures.Dimensions.ToArray());
        ReadOnlySpan<float> expected = ((DenseTensor<float>)fullFeatures).Buffer.Span;
        ReadOnlySpan<float> actual = ((DenseTensor<float>)shortFeatures).Buffer.Span;
        Assert.Equal(expected.Length, actual.Length);
        for (int index = 0; index < expected.Length; index++)
        {
            Assert.True(
                MathF.Abs(expected[index] - actual[index]) <= 1e-6f,
                $"Mel value differs at flat index {index}: expected {expected[index]}, actual {actual[index]}.");
        }
    }

    [Fact]
    public void Extract_empty_input_keeps_encoder_shape_and_computes_no_frames()
    {
        var extractor = new WhisperFeatureExtractor();

        DenseTensor<float> features = extractor.Extract(ReadOnlySpan<float>.Empty);

        Assert.Equal(0, extractor.LastComputedFrameCount);
        Assert.Equal([1, 80, 3000], features.Dimensions.ToArray());
    }

    private static float[] MakeSamples(int sampleCount)
    {
        var samples = new float[sampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = (float)(
                (0.5 * Math.Sin(((2 * Math.PI * 440 * index) / 16000)))
                + (0.25 * Math.Sin(((2 * Math.PI * 1237 * index) / 16000))));
        }

        return samples;
    }
}
