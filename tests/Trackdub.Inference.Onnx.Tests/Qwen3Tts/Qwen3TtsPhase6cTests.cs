using Trackdub.Inference.Onnx.Qwen3Tts.Audio;
using Trackdub.Inference.Onnx.Qwen3Tts.Models;

namespace Trackdub.Inference.Onnx.Tests.Qwen3Tts;

public sealed class Qwen3TtsPhase6cTests
{
    // M6: partial selection must select exactly what the full-sort reference selects.
    public static TheoryData<float[], float, int, float> SamplingCases()
    {
        var data = new TheoryData<float[], float, int, float>();
        float[] crafted = [3f, -1f, 3f, 0.5f, 3f, -20f, 2.999f, 0.5f, -1f, 1.25f];
        data.Add(crafted, 0.9f, 3, 1.0f);   // tie at the top-k cutoff
        data.Add(crafted, 0.9f, 1, 1.0f);   // single survivor
        data.Add(crafted, 1.0f, 0, 1.0f);   // top-k disabled
        data.Add(crafted, 1.0f, 50, 1.0f);  // top-k >= vocab
        data.Add(crafted, 0.7f, 4, 0.55f);  // top-p cuts inside top-k
        data.Add(crafted, 0.7f, 0, 0.30f);  // top-p without top-k
        data.Add(crafted, 1.3f, 5, 0.01f);  // top-p keeps only the argmax
        data.Add(crafted, 1.0f, 6, 0.999f); // top-p boundary near full mass
        data.Add([0f, 0f, 0f, 0f], 1.0f, 2, 0.5f); // all tied
        data.Add([float.NegativeInfinity, 1f, float.NegativeInfinity, 2f], 1.0f, 0, 0.9f);
        return data;
    }

    [Theory]
    [MemberData(nameof(SamplingCases))]
    public void Partial_sampling_selects_the_same_token_as_full_sort(float[] logits, float temperature, int topK, float topP)
    {
        foreach (float random in new[] { 0f, 0.05f, 0.2f, 0.42f, 0.61f, 0.83f, 0.999f })
        {
            int expected = FullSortReference(logits, temperature, topK, topP, random);
            int actual = TokenSampling.Sample(logits, temperature, topK, topP, random);
            Assert.Equal(expected, actual);
        }
    }

    private static int FullSortReference(float[] logits, float temperature, int topK, float topP, float random)
    {
        float[] probs = (float[])logits.Clone();
        if (temperature > 0)
        {
            for (int i = 0; i < probs.Length; i++)
            {
                probs[i] /= temperature;
            }
        }

        // Stable descending full sort (index tiebreak), as the pre-M6 LINQ code did.
        int[] sorted = Enumerable.Range(0, probs.Length)
            .OrderByDescending(i => probs[i])
            .ThenBy(i => i)
            .ToArray();
        var keep = new bool[probs.Length];
        int limit = topK > 0 && topK < probs.Length ? topK : probs.Length;
        for (int i = 0; i < limit; i++)
        {
            keep[sorted[i]] = true;
        }

        if (topP < 1.0f)
        {
            float max = probs.Where((_, i) => keep[i]).Max();
            double sum = probs.Where((_, i) => keep[i]).Sum(v => Math.Exp(v - max));
            double cumulative = 0;
            var nucleus = new bool[probs.Length];
            foreach (int idx in sorted.Where(i => keep[i]))
            {
                nucleus[idx] = true;
                cumulative += Math.Exp(probs[idx] - max) / sum;
                if (cumulative >= topP)
                {
                    break;
                }
            }

            keep = nucleus;
        }

        for (int i = 0; i < probs.Length; i++)
        {
            if (!keep[i])
            {
                probs[i] = float.NegativeInfinity;
            }
        }

        float maxLogit = probs.Max();
        double sumExp = 0;
        for (int i = 0; i < probs.Length; i++)
        {
            probs[i] = MathF.Exp(probs[i] - maxLogit);
            sumExp += probs[i];
        }

        double cum = 0;
        for (int i = 0; i < probs.Length; i++)
        {
            cum += probs[i] / sumExp;
            if (random < cum)
            {
                return i;
            }
        }

        return probs.Length - 1;
    }

    // M9: bulk-read WAV decoding must match sample-by-sample decoding exactly.
    [Fact]
    public void ReadWav_bulk_read_matches_generated_wav_samples()
    {
        string dir = Path.Join(Path.GetTempPath(), "trackdub-qwen-wav-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Stereo 16-bit with an odd-sized auxiliary chunk before data.
            short[] stereo = [0, 16384, 32767, -32768, -1000, 1000, 12345, -12345];
            string stereoPath = Path.Join(dir, "stereo.wav");
            File.WriteAllBytes(stereoPath, BuildWav(stereo, channels: 2, sampleRate: 24000, bitsPerSample: 16, includeOddChunk: true));
            var (stereoSamples, stereoRate) = MelSpectrogram.ReadWav(stereoPath);
            Assert.Equal(24000, stereoRate);
            Assert.Equal(
                [0.25f, ((32767 / 32768f) + -1f) / 2f, 0f, 0f],
                stereoSamples);

            // Mono float32.
            float[] mono = [0f, 0.25f, -0.75f, 1f];
            string monoPath = Path.Join(dir, "mono.wav");
            File.WriteAllBytes(monoPath, BuildWav(mono, channels: 1, sampleRate: 16000, bitsPerSample: 32, includeOddChunk: false));
            var (monoSamples, monoRate) = MelSpectrogram.ReadWav(monoPath);
            Assert.Equal(16000, monoRate);
            Assert.Equal(mono, monoSamples);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static byte[] BuildWav<T>(T[] samples, int channels, int sampleRate, short bitsPerSample, bool includeOddChunk)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        int bytesPerSample = bitsPerSample / 8;
        int dataSize = samples.Length * bytesPerSample;
        int extra = includeOddChunk ? 13 : 0; // "JUNK" + size + 5 payload bytes (odd)
        writer.Write("RIFF"u8);
        writer.Write(36 + extra + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bytesPerSample);
        writer.Write((short)(channels * bytesPerSample));
        writer.Write(bitsPerSample);
        if (includeOddChunk)
        {
            writer.Write("JUNK"u8);
            writer.Write(5);
            writer.Write(new byte[] { 1, 2, 3, 4, 5 });
        }

        writer.Write("data"u8);
        writer.Write(dataSize);
        foreach (T sample in samples)
        {
            switch (sample)
            {
                case short s:
                    writer.Write(s);
                    break;
                case float f:
                    writer.Write(f);
                    break;
                default:
                    throw new InvalidOperationException("Unsupported sample type.");
            }
        }

        writer.Flush();
        return stream.ToArray();
    }
}
