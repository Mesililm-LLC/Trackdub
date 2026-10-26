using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Contracts.Diagnostics;

namespace Trackdub.Inference.Onnx.Qwen3Tts.Models;

/// <summary>
/// Wraps the ECAPA-TDNN speaker encoder ONNX model.
/// Extracts a 1024-dimensional speaker embedding from a mel-spectrogram.
/// Input: (1, T_mel, 128) float32 — time-first mel-spectrogram.
/// Output: (1, 1024) float32 — speaker embedding (x-vector).
/// </summary>
public sealed class SpeakerEncoder : IDisposable
{
    private InferenceSession? _session;
    private readonly string _modelPath;
    private readonly Func<SessionOptions> _sessionOptionsFactory;
    private readonly QwenPooledSession? _pooledSession;

    /// <summary>Embedding dimension (1024 for ECAPA-TDNN in Qwen3-TTS).</summary>
    public int EmbeddingDim => 1024;

    /// <summary>Expected number of mel bins.</summary>
    public int MelDim => 128;

    public SpeakerEncoder(string modelPath, Func<SessionOptions>? sessionOptionsFactory = null)
        : this(modelPath, sessionOptionsFactory, pooledSession: null)
    {
    }

    internal SpeakerEncoder(string modelPath, Func<SessionOptions>? sessionOptionsFactory, QwenPooledSession? pooledSession)
    {
        _modelPath = modelPath;
        _sessionOptionsFactory = sessionOptionsFactory ?? CreateDefaultOptions;
        _pooledSession = pooledSession;
    }

    private static SessionOptions CreateDefaultOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
    };

    private InferenceSession GetSession()
    {
        if (_pooledSession is not null)
        {
            return _pooledSession.Session;
        }

        if (_session is null)
        {
            _session = new InferenceSession(_modelPath, _sessionOptionsFactory());
        }
        return _session;
    }

    /// <summary>
    /// Encode a mel-spectrogram into a speaker embedding.
    /// </summary>
    /// <param name="melSpectrogram">Mel-spectrogram of shape (T_mel, n_mels), time-first.</param>
    /// <returns>Speaker embedding of length 1024.</returns>
    public float[] Encode(float[,] melSpectrogram)
    {
        int tMel = melSpectrogram.GetLength(0);
        int nMels = melSpectrogram.GetLength(1);

        // Create tensor (1, T_mel, n_mels)
        var tensor = new DenseTensor<float>(new[] { 1, tMel, nMels });
        for (int t = 0; t < tMel; t++)
            for (int m = 0; m < nMels; m++)
                tensor[0, t, m] = melSpectrogram[t, m];

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("mel_spectrogram", tensor)
        };

        using var results = GetSession().Run(inputs);
        ColdStartCapture.MarkInferenceCompleted("qwen3-tts:speaker-encoder");
        var outputTensor = results.First().AsTensor<float>();

        // Copy to flat array
        var embedding = new float[outputTensor.Length];
        int i = 0;
        foreach (var val in outputTensor)
            embedding[i++] = val;

        return embedding;
    }

    /// <summary>
    /// Encode a WAV file into a speaker embedding.
    /// Convenience method that handles mel-spectrogram extraction.
    /// </summary>
    public float[] EncodeFromWav(string wavPath)
    {
        var mel = Audio.MelSpectrogram.FromWavFile(wavPath);
        return Encode(mel);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _pooledSession?.Dispose();
    }
}
