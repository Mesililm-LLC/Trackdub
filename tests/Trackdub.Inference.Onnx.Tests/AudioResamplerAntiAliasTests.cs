using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Onnx.Tests;

// M-H5: pipeline-normalized audio is 48 kHz, consumers target 16 kHz, so
// downsampling reaches AudioResampler in production and must not alias.
public sealed class AudioResamplerAntiAliasTests
{
    private static float[] Sine(double frequencyHz, int sampleRate, int samples, float amplitude = 0.9f)
    {
        var data = new float[samples];
        for (int i = 0; i < samples; i++)
        {
            data[i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequencyHz * i / sampleRate));
        }

        return data;
    }

    private static double Rms(ReadOnlySpan<float> data, int skip = 0)
    {
        double sum = 0;
        int count = 0;
        for (int i = skip; i < data.Length - skip; i++)
        {
            sum += data[i] * data[i];
            count++;
        }

        return Math.Sqrt(sum / count);
    }

    [Fact]
    public void Resample_AboveNyquistSine_DoesNotFoldIntoPassband()
    {
        // 12 kHz at 48 kHz is above the 8 kHz Nyquist of 16 kHz output; linear
        // interpolation would fold it to 4 kHz at near-full amplitude.
        float[] output = AudioResampler.Resample(Sine(12_000, 48_000, 48_000), 48_000, 16_000);
        Assert.Equal(16_000, output.Length);
        Assert.True(Rms(output, 100) < 0.05, $"Aliased RMS {Rms(output, 100):F4} should be < 0.05");
    }

    [Fact]
    public void Resample_SweepAboveNyquist_StaysSuppressed()
    {
        foreach (double frequency in new[] { 9_000d, 12_000d, 16_000d, 20_000d })
        {
            float[] output = AudioResampler.Resample(Sine(frequency, 48_000, 48_000), 48_000, 16_000);
            Assert.True(
                Rms(output, 100) < 0.05,
                $"{frequency} Hz aliased RMS {Rms(output, 100):F4} should be < 0.05");
        }
    }

    [Fact]
    public void Resample_PassbandSine_IsPreserved()
    {
        float[] output = AudioResampler.Resample(Sine(1000, 48_000, 48_000), 48_000, 16_000);
        double expected = 0.9 / Math.Sqrt(2);
        Assert.Equal(expected, Rms(output, 100), 0.05);
    }

    [Fact]
    public void Resample_SameRateAndUpsampling_UnchangedContract()
    {
        float[] input = Sine(1000, 16_000, 16_000);
        Assert.Equal(input, AudioResampler.Resample(input, 16_000, 16_000));
        float[] up = AudioResampler.Resample(input, 16_000, 48_000);
        Assert.Equal(48_000, up.Length);
        Assert.Equal(0.9 / Math.Sqrt(2), Rms(up, 100), 0.05);
    }

    [Fact]
    public void StreamingResampler_AboveNyquistSine_DoesNotFoldIntoPassband()
    {
        using IAudioSamples source = new ArrayAudioSamples(Sine(12_000, 48_000, 48_000), 48_000);
        using IAudioSamples resampled = AudioResampler.CreateResampledStream(source, 16_000);
        Assert.Equal(16_000, resampled.SampleRate);
        Assert.Equal(16_000, resampled.SampleFrameCount);
        var output = new float[16_000];
        resampled.ReadMonoSamples(0, output);
        Assert.True(Rms(output, 100) < 0.05, $"Streaming aliased RMS {Rms(output, 100):F4} should be < 0.05");

        using IAudioSamples passSource = new ArrayAudioSamples(Sine(1000, 48_000, 48_000), 48_000);
        using IAudioSamples passResampled = AudioResampler.CreateResampledStream(passSource, 16_000);
        var passOutput = new float[16_000];
        passResampled.ReadMonoSamples(0, passOutput);
        Assert.Equal(0.9 / Math.Sqrt(2), Rms(passOutput, 100), 0.05);
    }

    [Fact]
    public void StreamingResampler_ChunkedReadsMatchWholeRead()
    {
        float[] signal = Sine(1000, 48_000, 48_000);
        using IAudioSamples wholeSource = new ArrayAudioSamples(signal, 48_000);
        using IAudioSamples whole = AudioResampler.CreateResampledStream(wholeSource, 16_000);
        var wholeOutput = new float[16_000];
        whole.ReadMonoSamples(0, wholeOutput);

        using IAudioSamples chunkSource = new ArrayAudioSamples(signal, 48_000);
        using IAudioSamples chunked = AudioResampler.CreateResampledStream(chunkSource, 16_000);
        var chunkedOutput = new float[16_000];
        for (int offset = 0; offset < chunkedOutput.Length; offset += 1600)
        {
            chunked.ReadMonoSamples(offset, chunkedOutput.AsSpan(offset, 1600));
        }

        for (int i = 100; i < 15_900; i++)
        {
            Assert.Equal(wholeOutput[i], chunkedOutput[i], 0.002f);
        }
    }

    [Fact]
    public void ChannelResampler_AboveNyquistSine_DoesNotFoldIntoPassband()
    {
        // Channel mirror of the mono streaming test: ch0 is 12 kHz (above the
        // 8 kHz output Nyquist, would fold to 4 kHz unfiltered), ch1 is a
        // 1 kHz passband control proving the channel path filters rather
        // than mutes. Guards the ReadChannelSamples anti-alias branch.
        using IAudioChannelSamples source = new ArrayChannelAudioSamples(
            [Sine(12_000, 48_000, 48_000), Sine(1000, 48_000, 48_000)], 48_000);
        using IAudioChannelSamples resampled = AudioResampler.CreateResampledChannelStream(source, 16_000);
        Assert.Equal(16_000, resampled.SampleRate);
        Assert.Equal(16_000, resampled.SampleFrameCount);
        Assert.Equal(2, resampled.ChannelCount);

        var aliased = new float[16_000];
        resampled.ReadChannelSamples(0, 0, aliased);
        Assert.True(Rms(aliased, 100) < 0.05, $"Channel aliased RMS {Rms(aliased, 100):F4} should be < 0.05");

        var passband = new float[16_000];
        resampled.ReadChannelSamples(0, 1, passband);
        Assert.Equal(0.9 / Math.Sqrt(2), Rms(passband, 100), 0.05);
    }

    private sealed class ArrayChannelAudioSamples(float[][] channels, int sampleRate) : IAudioChannelSamples
    {
        public int SampleRate => sampleRate;

        public long SampleFrameCount => channels[0].Length;

        public int ChannelCount => channels.Length;

        public void ReadMonoSamples(long startFrame, Span<float> destination)
        {
            destination.Clear();
            var buffer = new float[destination.Length];
            foreach (float[] channel in channels)
            {
                ReadChannel(startFrame, channel, buffer);
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] += buffer[i] / channels.Length;
                }
            }
        }

        public void ReadChannelSamples(long startFrame, int channelIndex, Span<float> destination)
        {
            destination.Clear();
            ReadChannel(startFrame, channels[channelIndex], destination);
        }

        private static void ReadChannel(long startFrame, float[] samples, Span<float> destination)
        {
            if (startFrame < 0 || startFrame >= samples.Length)
            {
                return;
            }

            int count = (int)Math.Min(destination.Length, samples.Length - startFrame);
            samples.AsSpan((int)startFrame, count).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ArrayAudioSamples(float[] samples, int sampleRate) : IAudioSamples
    {
        public int SampleRate => sampleRate;

        public long SampleFrameCount => samples.Length;

        public void ReadMonoSamples(long startFrame, Span<float> destination)
        {
            destination.Clear();
            if (startFrame < 0 || startFrame >= samples.Length)
            {
                return;
            }

            int count = (int)Math.Min(destination.Length, samples.Length - startFrame);
            samples.AsSpan((int)startFrame, count).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }
}
