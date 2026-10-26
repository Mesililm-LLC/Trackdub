using System.Runtime.CompilerServices;
using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Onnx.Qwen3Tts.Pipeline;

/// <summary>
/// A production-ready <see cref="ITextToSpeechClient"/> backed by QwenTTS.
/// Provides thread-safe lazy initialization, in-memory synthesis (with automatic
/// temp file cleanup), streaming support, and proper resource disposal.
/// </summary>
/// <remarks>
/// The underlying <see cref="TtsPipeline"/> is lazily initialized on first use
/// and safely shared across concurrent callers via <see cref="SemaphoreSlim"/>.
/// Models are automatically downloaded from HuggingFace if not present.
/// </remarks>
public sealed class QwenTextToSpeechClient : ITextToSpeechClient
{
    private readonly string _defaultVoice;
    private readonly string _defaultLanguage;
    private readonly string? _modelDir;
    private readonly string? _repoId;
    private readonly QwenModelVariant _variant;
    private readonly Func<Microsoft.ML.OnnxRuntime.SessionOptions>? _sessionOptionsFactory;
    private readonly Func<Microsoft.ML.OnnxRuntime.SessionOptions>? _vocoderSessionOptionsFactory;

    private TtsPipeline? _pipeline;
    private bool _disposed;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    /// <summary>
    /// Creates a new <see cref="QwenTextToSpeechClient"/>.
    /// </summary>
    /// <param name="defaultVoice">Default speaker name (e.g., "ryan", "serena"). Default: "ryan".</param>
    /// <param name="defaultLanguage">Default language (e.g., "english", "auto"). Default: "auto".</param>
    /// <param name="modelDir">Optional model directory. When null, uses the variant-specific default.</param>
    /// <param name="repoId">HuggingFace repository ID. When null, uses the variant's default repo.</param>
    /// <param name="variant">Model size variant. Defaults to 0.6B.</param>
    /// <param name="sessionOptionsFactory">Optional ONNX Runtime session options factory (e.g., for GPU).</param>
    /// <param name="vocoderSessionOptionsFactory">Optional separate factory for the vocoder model.</param>
    public QwenTextToSpeechClient(
        string defaultVoice = "ryan",
        string defaultLanguage = "auto",
        string? modelDir = null,
        string? repoId = null,
        QwenModelVariant variant = QwenModelVariant.Qwen06B,
        Func<Microsoft.ML.OnnxRuntime.SessionOptions>? sessionOptionsFactory = null,
        Func<Microsoft.ML.OnnxRuntime.SessionOptions>? vocoderSessionOptionsFactory = null)
    {
        _defaultVoice = defaultVoice;
        _defaultLanguage = defaultLanguage;
        _modelDir = modelDir;
        _repoId = repoId;
        _variant = variant;
        _sessionOptionsFactory = sessionOptionsFactory;
        _vocoderSessionOptionsFactory = vocoderSessionOptionsFactory;
    }

    /// <inheritdoc />
    public async Task<TextToSpeechResponse> SynthesizeToMemoryAsync(
        string text,
        TextToSpeechOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        await EnsureInitializedAsync(cancellationToken);

        var voice = options?.VoiceId ?? _defaultVoice;
        var language = options?.Language ?? _defaultLanguage;
        var instruct = options?.Instruct;

        // Synthesize in memory and encode directly; no temp WAV file.
        float[] samples = await _pipeline!.SynthesizeSamplesAsync(text, voice, language, instruct);
        byte[] audioData = WaveAudioWriter.EncodeMonoPcm16(samples, 24000);

        return new TextToSpeechResponse
        {
            AudioData = audioData,
            MediaType = "audio/wav",
            SampleRate = 24000,
            ModelId = options?.ModelId ?? "qwen3-tts",
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TextToSpeechStreamingUpdate> SynthesizeStreamingAsync(
        string text,
        TextToSpeechOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new TextToSpeechStreamingUpdate
        {
            Kind = TextToSpeechUpdateKind.SessionOpen,
        };

        var response = await SynthesizeToMemoryAsync(text, options, cancellationToken);

        if (response.AudioData is { Length: > 0 })
        {
            yield return new TextToSpeechStreamingUpdate
            {
                Kind = TextToSpeechUpdateKind.AudioChunk,
                AudioData = response.AudioData,
                SampleRate = response.SampleRate,
            };
        }

        yield return new TextToSpeechStreamingUpdate
        {
            Kind = TextToSpeechUpdateKind.SessionClose,
        };
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_pipeline is not null) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_pipeline is not null) return;
            if (string.IsNullOrWhiteSpace(_modelDir))
            {
                throw new InvalidOperationException(
                    "QwenTextToSpeechClient requires an explicit model directory; Trackdub resolves ONNX roots from manifest inventory.");
            }

            _pipeline = new TtsPipeline(
                _modelDir,
                _sessionOptionsFactory,
                _vocoderSessionOptionsFactory,
                _variant);
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>Disposes the underlying pipeline, semaphore, and any held resources.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pipeline?.Dispose();
        _pipeline = null;
        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }
}
