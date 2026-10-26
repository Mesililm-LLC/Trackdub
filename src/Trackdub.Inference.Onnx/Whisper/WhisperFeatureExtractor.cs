using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Whisper;

internal sealed class WhisperFeatureExtractor
{
    private const int SampleRate = 16000;
    private const int FftSize = 400;
    private const int HopLength = 160;
    private const int MaxSamples = 480000;
    private const int MaxFrames = 3000;
    private const int FrequencyBins = 1 + (FftSize / 2);
    private readonly float[] hannWindow = BuildPeriodicHannWindow(FftSize);
    private readonly int melBins;
    private readonly float[,] melFilters;

    public WhisperFeatureExtractor(int melBins = 80)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(melBins);
        this.melBins = melBins;
        melFilters = BuildMelFilterBank(melBins);
    }

    public int MelBins => melBins;

    /// <summary>Frames the STFT/mel stages actually computed in the last <see cref="Extract"/> call.</summary>
    internal int LastComputedFrameCount { get; private set; }

    /// <summary>
    /// Frames whose centered STFT window can observe at least one actual sample: frame
    /// <c>f</c> covers <c>[f * HopLength - FftSize / 2, f * HopLength + FftSize / 2)</c>, so it is
    /// influenced iff <c>f * HopLength - FftSize / 2 &lt; sampleCount</c>. Every later frame sees
    /// only the zero padding the old path fed the encoder, so its mel value is the normalized
    /// silence floor and can be filled into the result instead of computed.
    /// </summary>
    internal static int ComputeFrameCount(int sampleCount)
    {
        int actualSamples = Math.Clamp(sampleCount, 0, MaxSamples);
        if (actualSamples == 0)
        {
            return 0;
        }

        return Math.Min(MaxFrames, ((actualSamples + (FftSize / 2) - 1) / HopLength) + 1);
    }

    public DenseTensor<float> Extract(ReadOnlySpan<float> inputSamples)
    {
        // The encoder input contract stays the fixed 30 s window: the TRT encoder profile is
        // built around opt/max 3000 frames (WhisperOnnxAudioTranscriptionEngine
        // .BuildTrtEncoderOptions) and the decoder always consumes 1500 encoder positions.
        // Only the mel computation shrinks; the result is padded with the silence floor that
        // the full computation would have produced for the uncomputed frames.
        int actualSamples = Math.Min(inputSamples.Length, MaxSamples);
        int computedFrames = ComputeFrameCount(inputSamples.Length);
        LastComputedFrameCount = computedFrames;

        float[]? rentedStftSamples = null;
        try
        {
            ReadOnlySpan<float> stftSamples = BuildStftSamples(
                inputSamples.Slice(0, actualSamples), computedFrames, out rentedStftSamples);
            float[,] powerSpectrum = ComputePowerSpectrum(stftSamples, computedFrames);
            float[,] melSpectrum = ApplyMelFilters(powerSpectrum, computedFrames);
            float silenceValue = NormalizeLogMel(melSpectrum, computedFrames);

            var data = new float[melBins * MaxFrames];
            for (int melIndex = 0; melIndex < melBins; melIndex++)
            {
                int rowOffset = melIndex * MaxFrames;
                for (int frameIndex = 0; frameIndex < computedFrames; frameIndex++)
                {
                    data[rowOffset + frameIndex] = melSpectrum[melIndex, frameIndex];
                }

                for (int frameIndex = computedFrames; frameIndex < MaxFrames; frameIndex++)
                {
                    data[rowOffset + frameIndex] = silenceValue;
                }
            }

            return new DenseTensor<float>(data, [1, melBins, MaxFrames]);
        }
        finally
        {
            if (rentedStftSamples != null)
                System.Buffers.ArrayPool<float>.Shared.Return(rentedStftSamples);
        }
    }

    public static float[] PrepareSamples(float[] inputSamples, int inputSampleRate) =>
        inputSampleRate == SampleRate
            ? inputSamples
            : Audio.AudioResampler.Resample(inputSamples, inputSampleRate, SampleRate);

    /// <summary>
    /// Builds exactly the reflect-padded samples the first <paramref name="frameCount"/> STFT
    /// windows read in the old full-window path (pad/trim to 30 s, then reflect-pad by
    /// FftSize / 2 on both sides), without materializing the 30 s buffers. Positions past the
    /// actual samples read the same zeros the old zero-padding supplied; the far-end reflection
    /// is reproduced virtually so the full-window case stays identical too.
    /// </summary>
    private static ReadOnlySpan<float> BuildStftSamples(
        ReadOnlySpan<float> actualSamples, int frameCount, out float[]? rentedArray)
    {
        if (frameCount == 0)
        {
            rentedArray = null;
            return ReadOnlySpan<float>.Empty;
        }

        int padding = FftSize / 2;
        int targetLength = ((frameCount - 1) * HopLength) + FftSize;
        rentedArray = System.Buffers.ArrayPool<float>.Shared.Rent(targetLength);
        for (int index = 0; index < targetLength; index++)
        {
            int sourceIndex;
            if (index < padding)
            {
                sourceIndex = Math.Min(padding - index, MaxSamples - 1);
            }
            else if (index < padding + MaxSamples)
            {
                sourceIndex = index - padding;
            }
            else
            {
                sourceIndex = Math.Max(MaxSamples - 2 - (index - padding - MaxSamples), 0);
            }

            rentedArray[index] = sourceIndex < actualSamples.Length ? actualSamples[sourceIndex] : 0f;
        }

        return new ReadOnlySpan<float>(rentedArray, 0, targetLength);
    }

    private float[,] ComputePowerSpectrum(ReadOnlySpan<float> paddedSamples, int frameCount)
    {
        var result = new float[FrequencyBins, frameCount];
        var spectrum = new Complex[FftSize];

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int sampleOffset = frameIndex * HopLength;
            Array.Clear(spectrum);
            for (int sampleIndex = 0; sampleIndex < FftSize; sampleIndex++)
            {
                double windowed = paddedSamples[sampleOffset + sampleIndex] * hannWindow[sampleIndex];
                spectrum[sampleIndex] = new Complex(windowed, 0);
            }

            Fourier.Forward(spectrum, FourierOptions.Matlab);

            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double magnitude = spectrum[binIndex].Magnitude;
                result[binIndex, frameIndex] = (float)(magnitude * magnitude);
            }
        }

        return result;
    }

    private float[,] ApplyMelFilters(float[,] powerSpectrum, int frameCount)
    {
        var melSpectrum = new float[melBins, frameCount];
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                double sum = 0;
                for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                {
                    sum += melFilters[melIndex, binIndex] * powerSpectrum[binIndex, frameIndex];
                }

                melSpectrum[melIndex, frameIndex] = (float)sum;
            }
        }

        return melSpectrum;
    }

    /// <summary>Normalizes in place and returns the normalized value of a silent (zero-power) frame.</summary>
    private float NormalizeLogMel(float[,] melSpectrum, int frameCount)
    {
        // A zero-power frame clamps to 1e-10 before the log, so its log value is the floor
        // every uncomputed padded frame had in the full computation. Seeding the max with it
        // keeps the global max unchanged: computed frames can only reach it, never go below.
        float silenceLogValue = MathF.Log10(1e-10f);
        float maxValue = silenceLogValue;
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float clamped = Math.Max(melSpectrum[melIndex, frameIndex], 1e-10f);
                float logValue = MathF.Log10(clamped);
                melSpectrum[melIndex, frameIndex] = logValue;
                if (logValue > maxValue)
                {
                    maxValue = logValue;
                }
            }
        }

        float minimumAllowed = maxValue - 8f;
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float normalized = Math.Max(melSpectrum[melIndex, frameIndex], minimumAllowed);
                melSpectrum[melIndex, frameIndex] = (normalized + 4f) / 4f;
            }
        }

        return (Math.Max(silenceLogValue, minimumAllowed) + 4f) / 4f;
    }

    private static float[] BuildPeriodicHannWindow(int length)
    {
        var window = new float[length];
        for (int index = 0; index < length; index++)
        {
            window[index] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / length)));
        }

        return window;
    }

    private static float[,] BuildMelFilterBank(int melBins)
    {
        var filters = new float[melBins, FrequencyBins];
        double[] fftFrequencies = Enumerable.Range(0, FrequencyBins)
            .Select(index => index * (SampleRate / 2d) / (FrequencyBins - 1))
            .ToArray();

        double melMin = HertzToMel(0);
        double melMax = HertzToMel(SampleRate / 2d);
        double[] melPoints = Enumerable.Range(0, melBins + 2)
            .Select(index => melMin + ((melMax - melMin) * index / (melBins + 1d)))
            .ToArray();
        double[] hzPoints = melPoints.Select(MelToHertz).ToArray();

        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            double lower = hzPoints[melIndex];
            double center = hzPoints[melIndex + 1];
            double upper = hzPoints[melIndex + 2];
            double enorm = 2.0 / Math.Max(upper - lower, double.Epsilon);

            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double frequency = fftFrequencies[binIndex];
                double lowerSlope = (frequency - lower) / Math.Max(center - lower, double.Epsilon);
                double upperSlope = (upper - frequency) / Math.Max(upper - center, double.Epsilon);
                double weight = Math.Max(0, Math.Min(lowerSlope, upperSlope));
                filters[melIndex, binIndex] = (float)(weight * enorm);
            }
        }

        return filters;
    }

    private static double HertzToMel(double frequencyHertz)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4) / 27d;

        return frequencyHertz >= minLogHertz
            ? minLogMel + (Math.Log(frequencyHertz / minLogHertz) / logStep)
            : frequencyHertz / fSp;
    }

    private static double MelToHertz(double mel)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4) / 27d;

        return mel >= minLogMel
            ? minLogHertz * Math.Exp(logStep * (mel - minLogMel))
            : mel * fSp;
    }
}
