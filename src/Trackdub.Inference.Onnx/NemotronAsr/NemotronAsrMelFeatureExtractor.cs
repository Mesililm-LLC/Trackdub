using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal sealed class NemotronAsrMelFeatureExtractor
{
    public const int SampleRate = 16_000;
    public const int MelBins = 128;
    public const int ChunkFrames = 56;
    public const int PreEncodeCacheFrames = 9;
    public const int ChunkInputFrames = PreEncodeCacheFrames + ChunkFrames;

    private const int FftSize = 512;
    private const int WinLength = 400;
    private const int HopLength = 160;
    private const float PreEmphasis = 0.97f;
    private const float LogZeroGuard = 5.9604645e-8f;
    private const int FrequencyBins = 1 + (FftSize / 2);

    // torch.stft centers a window shorter than n_fft inside the FFT frame.
    private const int WindowOffset = (FftSize - WinLength) / 2;

    private readonly float[] hannWindow = BuildHannWindow(WinLength);
    private readonly float[,] melFilters = BuildMelFilterBank();
    private readonly bool applyPerFeatureNormalization;

    private static int melFilterBankBuildCount;

    /// <summary>Instrumentation seam: how many mel filterbanks have been constructed.</summary>
    internal static int MelFilterBankBuildCountForTesting => Volatile.Read(ref melFilterBankBuildCount);

    public NemotronAsrMelFeatureExtractor(bool applyPerFeatureNormalization = false)
    {
        // Bundled Nemotron export config sets preprocessor.normalize = "NA" (no per-feature
        // mean/std). Default false so callers that skip config stay aligned with the model.
        this.applyPerFeatureNormalization = applyPerFeatureNormalization;
    }

    public float[,] Extract(ReadOnlySpan<float> inputSamples)
    {
        if (inputSamples.Length == 0)
        {
            return new float[MelBins, 0];
        }

        float[] emphasized = ApplyPreEmphasis(inputSamples);
        float[] padded = ZeroPad(emphasized, FftSize / 2);
        // NeMo keeps floor(n / hop) frames; torch.stft's trailing frame is masked out.
        int frameCount = inputSamples.Length / HopLength;
        if (frameCount == 0)
        {
            return new float[MelBins, 0];
        }

        float[,] powerSpectrum = ComputePowerSpectrum(padded, frameCount);
        var mel = new float[MelBins, frameCount];
        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                double sum = 0;
                for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                {
                    sum += melFilters[melIndex, binIndex] * powerSpectrum[binIndex, frameIndex];
                }

                mel[melIndex, frameIndex] = MathF.Log((float)sum + LogZeroGuard);
            }
        }

        if (applyPerFeatureNormalization)
        {
            NormalizePerFeature(mel, MelBins, frameCount);
        }

        return mel;
    }

    // Static: chunk assembly only slices the finished mel and uses no extractor state.
    // The decoder calls this, so it must not construct an extractor (which would rebuild
    // the mel filterbank and Hann window a second time for the same region).
    public static float[] BuildChunk(float[,] mel, int frameOffset, int mainFrameCount, bool includePreEncodeCache)
    {
        var data = new float[MelBins * ChunkInputFrames];
        if (includePreEncodeCache && frameOffset >= PreEncodeCacheFrames)
        {
            int cacheStart = frameOffset - PreEncodeCacheFrames;
            for (int frame = 0; frame < PreEncodeCacheFrames; frame++)
            {
                CopyFrame(mel, cacheStart + frame, data, frame);
            }
        }

        for (int frame = 0; frame < mainFrameCount; frame++)
        {
            CopyFrame(mel, frameOffset + frame, data, PreEncodeCacheFrames + frame);
        }

        return data;
    }

    private static void CopyFrame(float[,] mel, int sourceFrame, float[] destination, int destinationFrame)
    {
        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            destination[(melIndex * ChunkInputFrames) + destinationFrame] = mel[melIndex, sourceFrame];
        }
    }

    private float[,] ComputePowerSpectrum(float[] paddedSamples, int frameCount)
    {
        var result = new float[FrequencyBins, frameCount];
        var spectrum = new Complex[FftSize];

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int sampleOffset = frameIndex * HopLength;
            Array.Clear(spectrum);
            for (int sampleIndex = 0; sampleIndex < WinLength; sampleIndex++)
            {
                spectrum[WindowOffset + sampleIndex] = new Complex(
                    paddedSamples[sampleOffset + WindowOffset + sampleIndex] * hannWindow[sampleIndex],
                    0d);
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

    private static void NormalizePerFeature(float[,] mel, int melBins, int frameCount)
    {
        const float Epsilon = 1e-5f;
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            double sum = 0;
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                sum += mel[melIndex, frameIndex];
            }

            float mean = (float)(sum / frameCount);

            double variance = 0;
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                double diff = mel[melIndex, frameIndex] - mean;
                variance += diff * diff;
            }

            float std = MathF.Sqrt((float)(variance / frameCount) + Epsilon);

            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                mel[melIndex, frameIndex] = (mel[melIndex, frameIndex] - mean) / std;
            }
        }
    }

    private static float[] ApplyPreEmphasis(ReadOnlySpan<float> samples)
    {
        var result = new float[samples.Length];
        result[0] = samples[0];
        for (int index = 1; index < samples.Length; index++)
        {
            result[index] = samples[index] - (PreEmphasis * samples[index - 1]);
        }

        return result;
    }

    private static float[] ZeroPad(float[] samples, int padding)
    {
        var padded = new float[samples.Length + (padding * 2)];
        samples.CopyTo(padded.AsSpan(padding));
        return padded;
    }

    private static float[] BuildHannWindow(int length)
    {
        var window = new float[length];
        for (int index = 0; index < length; index++)
        {
            window[index] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / (length - 1))));
        }

        return window;
    }

    private static float[,] BuildMelFilterBank()
    {
        Interlocked.Increment(ref melFilterBankBuildCount);
        var filters = new float[MelBins, FrequencyBins];
        double[] fftFrequencies = Enumerable.Range(0, FrequencyBins)
            .Select(static index => index * (SampleRate / (double)FftSize))
            .ToArray();
        double melMin = HertzToMelSlaney(0d);
        double melMax = HertzToMelSlaney(SampleRate / 2d);
        double[] melPoints = Enumerable.Range(0, MelBins + 2)
            .Select(index => melMin + ((melMax - melMin) * index / (MelBins + 1d)))
            .Select(MelToHertzSlaney)
            .ToArray();
        double[] differences = melPoints.Zip(melPoints.Skip(1), static (left, right) => right - left).ToArray();

        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double frequency = fftFrequencies[binIndex];
                double lower = (frequency - melPoints[melIndex]) / Math.Max(differences[melIndex], double.Epsilon);
                double upper = (melPoints[melIndex + 2] - frequency) / Math.Max(differences[melIndex + 1], double.Epsilon);
                double weight = Math.Max(0d, Math.Min(lower, upper));
                double normalization = 2d / Math.Max(melPoints[melIndex + 2] - melPoints[melIndex], double.Epsilon);
                filters[melIndex, binIndex] = (float)(weight * normalization);
            }
        }

        return filters;
    }

    private static double HertzToMelSlaney(double frequencyHertz)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        const double minLogMel = minLogHertz / fSp;
        const double logStep = 0.06875177742094912d;

        return frequencyHertz >= minLogHertz
            ? minLogMel + (Math.Log(frequencyHertz / minLogHertz) / logStep)
            : frequencyHertz / fSp;
    }

    private static double MelToHertzSlaney(double mel)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        const double minLogMel = minLogHertz / fSp;
        const double logStep = 0.06875177742094912d;

        return mel >= minLogMel
            ? minLogHertz * Math.Exp(logStep * (mel - minLogMel))
            : mel * fSp;
    }
}
