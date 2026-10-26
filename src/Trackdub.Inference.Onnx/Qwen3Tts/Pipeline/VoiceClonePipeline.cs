using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.Qwen3Tts.Audio;
using Trackdub.Inference.Onnx.Qwen3Tts.Models;
using Trackdub.Inference.Onnx.Qwen3Tts.Pipeline;

namespace Trackdub.Inference.Onnx.Qwen3Tts.Pipeline;

/// <summary>
/// Base-model voice-cloning pipeline: reference audio → speaker embedding → talker LM → vocoder → WAV.
/// </summary>
/// <remarks>
/// <para>Requires a Qwen3-TTS <b>Base</b> bundle with <c>speaker_encoder.onnx</c>. Optional ICL mode
/// also loads <c>SpeechTokenizer</c> to supply reference transcript + codec frames.</para>
/// <para>Codec output matches CustomVoice: 16 RVQ indices at 12&nbsp;Hz, decoded to 24&nbsp;kHz PCM.</para>
/// <para>Implements <see cref="IDisposable"/>; disposes all ONNX sessions and embedding tables.</para>
/// </remarks>
public sealed class VoiceClonePipeline : IDisposable
{
    private readonly TextTokenizer _tokenizer;
    private readonly LanguageModel _languageModel;
    private readonly Vocoder _vocoder;
    private readonly EmbeddingStore _embeddings;
    private readonly SpeakerEncoder _speakerEncoder;
    private readonly string _modelDir;
    private readonly Func<SessionOptions>? _sessionOptionsFactory;
    private SpeechTokenizer? _speechTokenizer;

    /// <summary>
    /// Creates a VoiceClonePipeline from a local model directory.
    /// </summary>
    /// <param name="modelDir">Directory containing ONNX models, embeddings, and tokenizer.</param>
    /// <param name="sessionOptionsFactory">Optional factory for ONNX Runtime session options (e.g., for GPU acceleration). When null, uses CPU with max optimization.</param>
    public VoiceClonePipeline(string modelDir, Func<SessionOptions>? sessionOptionsFactory = null)
        : this(modelDir, sessionOptionsFactory, pooledSessions: null)
    {
    }

    internal VoiceClonePipeline(string modelDir, Func<SessionOptions>? sessionOptionsFactory, Qwen3TtsPooledSessions? pooledSessions)
    {
        _modelDir = modelDir;
        _sessionOptionsFactory = sessionOptionsFactory;

        var tokenizerDir = Path.Join(modelDir, "tokenizer");
        var embeddingsDir = Path.Join(modelDir, "embeddings");
        var configPath = Path.Join(embeddingsDir, "config.json");
        var speakerEncoderPath = Path.Join(modelDir, "speaker_encoder.onnx");

        if (!File.Exists(speakerEncoderPath))
            throw new FileNotFoundException(
                "Speaker encoder model not found. Use the Base model (not CustomVoice) for voice cloning.",
                speakerEncoderPath);

        _tokenizer = new TextTokenizer(tokenizerDir);
        _embeddings = new EmbeddingStore(embeddingsDir, configPath);
        _languageModel = new LanguageModel(modelDir, _embeddings, sessionOptionsFactory, pooledSessions);
        _vocoder = new Vocoder(Path.Join(modelDir, "vocoder.onnx"), sessionOptionsFactory, pooledSessions?.Vocoder);
        _speakerEncoder = new SpeakerEncoder(speakerEncoderPath, sessionOptionsFactory, pooledSessions?.SpeakerEncoder);
    }

    /// <summary>
    /// Extract a speaker embedding from a reference audio WAV file.
    /// The returned embedding can be reused across multiple synthesis calls
    /// with the same voice.
    /// </summary>
    /// <param name="referenceAudioPath">Path to a WAV file (3+ seconds recommended, 24 kHz mono preferred).</param>
    /// <returns>Speaker embedding float array (1024 dimensions).</returns>
    public float[] ExtractSpeakerEmbedding(string referenceAudioPath)
    {
        return _speakerEncoder.EncodeFromWav(referenceAudioPath);
    }

    /// <summary>
    /// Synthesize speech with a cloned voice from reference audio.
    /// </summary>
    /// <param name="text">Text to synthesize.</param>
    /// <param name="referenceAudioPath">Path to a reference WAV file for voice cloning.</param>
    /// <param name="outputPath">Path to save the output WAV file.</param>
    /// <param name="language">Language code (e.g., "english", "chinese", "auto").</param>
    /// <param name="progress">Optional progress callback.</param>
    public async Task SynthesizeAsync(string text, string referenceAudioPath, string outputPath,
                                      string language = "auto", IProgress<string>? progress = null)
    {
        // Step 1: Extract speaker embedding from reference audio
        progress?.Report("Extracting speaker embedding from reference audio...");
        var speakerEmbedding = ExtractSpeakerEmbedding(referenceAudioPath);
        progress?.Report($"Speaker embedding extracted ({speakerEmbedding.Length} dimensions)");

        // Step 2: Synthesize using the extracted embedding
        await SynthesizeWithEmbeddingAsync(text, speakerEmbedding, outputPath, language, progress);
    }

    /// <summary>
    /// Synthesize speech with a cloned voice using ICL (In-Context Learning) mode.
    /// When refText is provided, the model uses both reference text and reference audio codes
    /// for higher-quality voice cloning compared to speaker-embedding-only mode.
    /// </summary>
    /// <param name="text">Text to synthesize.</param>
    /// <param name="referenceAudioPath">Path to a reference WAV file for voice cloning.</param>
    /// <param name="outputPath">Path to save the output WAV file.</param>
    /// <param name="refText">Transcript of the reference audio. Enables ICL mode when provided.</param>
    /// <param name="language">Language code (e.g., "english", "chinese", "auto").</param>
    /// <param name="progress">Optional progress callback.</param>
    public async Task SynthesizeAsync(string text, string referenceAudioPath, string outputPath,
                                      string? refText, string language = "auto", IProgress<string>? progress = null)
    {
        // Step 1: Extract speaker embedding from reference audio
        progress?.Report("Extracting speaker embedding from reference audio...");
        var speakerEmbedding = ExtractSpeakerEmbedding(referenceAudioPath);
        progress?.Report($"Speaker embedding extracted ({speakerEmbedding.Length} dimensions)");

        if (refText != null)
        {
            // ICL mode: also extract audio codes from reference audio
            progress?.Report("Encoding reference audio for ICL mode...");
            var speechTokenizer = GetSpeechTokenizer();
            var refAudioCodes = speechTokenizer.EncodeFromWav(referenceAudioPath);
            int tFrames = refAudioCodes.GetLength(1);
            progress?.Report($"Reference audio encoded ({tFrames} frames)");

            // Tokenize reference text
            var refTokenIds = _tokenizer.Encode(refText);
            progress?.Report($"Reference text tokenized ({refTokenIds.Length} tokens)");

            await SynthesizeWithEmbeddingAsync(text, speakerEmbedding, outputPath,
                refText: refText, refAudioCodes: refAudioCodes,
                language: language, progress: progress);
        }
        else
        {
            await SynthesizeWithEmbeddingAsync(text, speakerEmbedding, outputPath, language, progress);
        }
    }

    /// <summary>
    /// Synthesize speech using a pre-extracted speaker embedding.
    /// Use this when synthesizing multiple utterances with the same cloned voice
    /// to avoid re-encoding the reference audio each time.
    /// </summary>
    public async Task SynthesizeWithEmbeddingAsync(string text, float[] speakerEmbedding, string outputPath,
                                                    string language = "auto", IProgress<string>? progress = null)
    {
        await SynthesizeWithEmbeddingAsync(text, speakerEmbedding, outputPath,
            refText: null, refAudioCodes: null, language: language, progress: progress);
    }

    /// <summary>
    /// Synthesize speech using a pre-extracted speaker embedding with optional ICL reference data.
    /// When refText and refAudioCodes are provided, enables In-Context Learning mode.
    /// </summary>
    /// <param name="text">Text to synthesize.</param>
    /// <param name="speakerEmbedding">Pre-extracted 1024-dim speaker embedding.</param>
    /// <param name="outputPath">Path to save the output WAV file.</param>
    /// <param name="refText">Optional reference text transcript for ICL mode.</param>
    /// <param name="refAudioCodes">Optional reference audio codes [1, T, 16] for ICL mode.</param>
    /// <param name="language">Language code (e.g., "english", "chinese", "auto").</param>
    /// <param name="progress">Optional progress callback.</param>
    public async Task SynthesizeWithEmbeddingAsync(string text, float[] speakerEmbedding, string outputPath,
                                                    string? refText = null, long[,,]? refAudioCodes = null,
                                                    string language = "auto", IProgress<string>? progress = null)
    {
        var tokenIds = _tokenizer.BuildCustomVoicePrompt(text, "none", language, instruct: null);
        progress?.Report($"Tokenized input ({tokenIds.Length} tokens)");

        // Tokenize reference text for ICL mode if provided
        int[]? refTokenIds = null;
        if (refText != null)
        {
            refTokenIds = _tokenizer.Encode(refText);
            progress?.Report($"ICL mode: {refTokenIds.Length} ref text tokens, {refAudioCodes?.GetLength(1) ?? 0} ref audio frames");
        }

        // Generate RVQ codec frames via LM
        progress?.Report("Running language model inference...");
        long[,,] generatedCodecCodes;
        if (refTokenIds != null && refAudioCodes != null)
        {
            generatedCodecCodes = _languageModel.GenerateWithSpeakerEmbeddingAndRefText(
                tokenIds, speakerEmbedding, language,
                refTokenIds: refTokenIds, refAudioCodes: refAudioCodes);
        }
        else
        {
            generatedCodecCodes = _languageModel.GenerateWithSpeakerEmbedding(tokenIds, speakerEmbedding, language);
        }

        int timesteps = generatedCodecCodes.GetLength(2);
        if (timesteps == 0)
            throw new InvalidOperationException("Language model produced no audio codec frames.");
        progress?.Report($"Generated {timesteps} audio frames");

        // Decode to waveform via vocoder
        progress?.Report("Decoding waveform via vocoder...");
        var waveform = _vocoder.Decode(generatedCodecCodes);

        // Write WAV file
        progress?.Report("Writing WAV file...");
        await WavWriter.WriteAsync(outputPath, waveform, sampleRate: 24000);

        var duration = waveform.Length / 24000.0;
        progress?.Report($"Saved {Path.GetFileName(outputPath)} ({waveform.Length} samples, {duration:F2}s)");
    }

    /// <summary>
    /// Gets or lazily creates the speech tokenizer for ICL mode.
    /// The speech tokenizer ONNX model is optional — only needed when refText is provided.
    /// </summary>
    private SpeechTokenizer GetSpeechTokenizer()
    {
        if (_speechTokenizer != null)
            return _speechTokenizer;

        var modelPath = Path.Join(_modelDir, "tokenizer12hz_encode.onnx");
        if (!File.Exists(modelPath))
            throw new FileNotFoundException(
                "Speech tokenizer model not found. Required for ICL (ref_text) mode. " +
                "Re-download the model or update VoiceCloningDownloader to include tokenizer12hz_encode.onnx.",
                modelPath);

        _speechTokenizer = new SpeechTokenizer(modelPath, _sessionOptionsFactory);
        return _speechTokenizer;
    }

    public void Dispose()
    {
        _tokenizer.Dispose();
        _embeddings.Dispose();
        _languageModel.Dispose();
        _vocoder.Dispose();
        _speakerEncoder.Dispose();
        _speechTokenizer?.Dispose();
    }
}
