using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.Qwen3Tts.Audio;
using Trackdub.Inference.Onnx.Qwen3Tts.Models;

namespace Trackdub.Inference.Onnx.Qwen3Tts.Pipeline;

/// <summary>
/// CustomVoice preset pipeline: text tokenization, talker LM RVQ generation, vocoder decode, WAV export.
/// </summary>
/// <remarks>
/// <para>Stages: <c>TextTokenizer</c> → <c>LanguageModel</c> (12&nbsp;Hz codec frames) →
/// <c>Vocoder</c> (24&nbsp;kHz PCM) → <see cref="Audio.WavWriter"/>.</para>
/// <para>Speakers come from bundled embedding tables. Unknown names fail fast via
/// <see cref="Models.EmbeddingStore.GetSpeakerId(string)"/> before inference starts.</para>
/// <para>Implements <see cref="IDisposable"/>; disposes tokenizer, embeddings, LM, and vocoder sessions.</para>
/// </remarks>
public sealed class TtsPipeline : ITtsPipeline
{
    private readonly TextTokenizer _tokenizer;
    private readonly LanguageModel _languageModel;
    private readonly Vocoder _vocoder;
    private readonly EmbeddingStore _embeddings;
    private readonly QwenModelVariant _variant;

    /// <summary>
    /// Creates a TtsPipeline from a local model directory.
    /// </summary>
    /// <param name="modelDir">Directory containing ONNX models, embeddings, and tokenizer.</param>
    /// <param name="sessionOptionsFactory">Optional factory for ONNX Runtime session options (e.g., for GPU acceleration). When null, uses CPU with max optimization.</param>
    /// <param name="vocoderSessionOptionsFactory">Optional separate factory for the vocoder model. Useful when GPU EP doesn't support vocoder ops (e.g., DirectML). When null, uses sessionOptionsFactory.</param>
    /// <param name="variant">Model size variant. Used to determine feature support (e.g., instruction control).</param>
    public TtsPipeline(string modelDir, Func<SessionOptions>? sessionOptionsFactory = null, Func<SessionOptions>? vocoderSessionOptionsFactory = null, QwenModelVariant variant = QwenModelVariant.Qwen06B)
        : this(modelDir, sessionOptionsFactory, vocoderSessionOptionsFactory, variant, pooledSessions: null)
    {
    }

    internal TtsPipeline(string modelDir, Func<SessionOptions>? sessionOptionsFactory, Func<SessionOptions>? vocoderSessionOptionsFactory, QwenModelVariant variant, Qwen3TtsPooledSessions? pooledSessions)
    {
        var tokenizerDir = Path.Join(modelDir, "tokenizer");
        var embeddingsDir = Path.Join(modelDir, "embeddings");
        var configPath = Path.Join(embeddingsDir, "config.json");

        _variant = variant;
        _tokenizer = new TextTokenizer(tokenizerDir);
        _embeddings = new EmbeddingStore(embeddingsDir, configPath);
        _languageModel = new LanguageModel(modelDir, _embeddings, sessionOptionsFactory, pooledSessions);
        _vocoder = new Vocoder(Path.Join(modelDir, "vocoder.onnx"), vocoderSessionOptionsFactory ?? sessionOptionsFactory, pooledSessions?.Vocoder);
    }

    /// <summary>Available speaker names from the model.</summary>
    public IReadOnlyCollection<string> Speakers => _embeddings.GetAvailableSpeakers();

    /// <summary>The model variant this pipeline was created with.</summary>
    public QwenModelVariant ModelVariant => _variant;

    /// <summary>
    /// Synthesizes speech from text and saves the output to a WAV file.
    /// </summary>
    /// <param name="text">Input text to synthesize. Must not be null, empty, and cannot exceed 10,000 characters.</param>
    /// <param name="speaker">Speaker name (must exist in model embeddings).</param>
    /// <param name="outputPath">Path where the output WAV file will be saved.</param>
    /// <param name="language">Language code (default: "auto" for auto-detection).</param>
    /// <param name="instruct">Optional instruction prompt for voice style modification.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <exception cref="ArgumentNullException">Thrown when text is null.</exception>
    /// <exception cref="ArgumentException">Thrown when text is empty or exceeds 10,000 characters.</exception>
    public async Task SynthesizeAsync(string text, string speaker, string outputPath,
                                     string language = "auto", string? instruct = null,
                                     IProgress<string>? progress = null)
    {
        var waveform = SynthesizeSamples(text, speaker, language, instruct, progress);

        // Write WAV file
        progress?.Report("Writing WAV file...");
        await WavWriter.WriteAsync(outputPath, waveform, sampleRate: 24000);

        var duration = waveform.Length / 24000.0;
        progress?.Report($"Saved {Path.GetFileName(outputPath)} ({waveform.Length} samples, {duration:F2}s)");
    }

    /// <summary>
    /// Synthesizes speech and returns the 24 kHz PCM samples in memory, without any
    /// temp WAV file round-trip.
    /// </summary>
    public Task<float[]> SynthesizeSamplesAsync(string text, string speaker,
                                     string language = "auto", string? instruct = null,
                                     IProgress<string>? progress = null) =>
        Task.FromResult(SynthesizeSamples(text, speaker, language, instruct, progress));

    private float[] SynthesizeSamples(string text, string speaker,
                                     string language, string? instruct,
                                     IProgress<string>? progress)
    {
        // Input validation
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0)
            throw new ArgumentException("Text cannot be empty.", nameof(text));
        if (text.Length > 10000)
            throw new ArgumentException("Text exceeds maximum length of 10,000 characters.", nameof(text));

        _embeddings.GetSpeakerId(speaker.ToLowerInvariant());

        // Variant-aware instruct handling: 0.6B does not support instruction control
        if (!string.IsNullOrEmpty(instruct) && !QwenModelVariantConfig.SupportsInstruct(_variant))
        {
            var warning = $"Warning: Instruction text ignored \u2014 {_variant} model does not support instruction control. Use 1.7B for style instructions.";
            progress?.Report(warning);
            instruct = null;
        }

        // Build prompt using tokenizer
        var tokenIds = _tokenizer.BuildCustomVoicePrompt(text, speaker, language, instruct);

        progress?.Report($"Tokenized input ({tokenIds.Length} tokens)");

        // Generate RVQ codec frames via LM
        progress?.Report("Running language model inference...");
        var generatedCodecCodes = _languageModel.Generate(tokenIds, speaker, language);

        int timesteps = generatedCodecCodes.GetLength(2);
        if (timesteps == 0)
            throw new InvalidOperationException("Language model produced no audio codec frames.");
        progress?.Report($"Generated {timesteps} audio frames");

        // Decode to waveform via vocoder
        progress?.Report("Decoding waveform via vocoder...");
        return _vocoder.Decode(generatedCodecCodes);
    }

    /// <summary>
    /// Synthesizes speech using a strongly-typed voice preset.
    /// </summary>
    /// <param name="text">Input text to synthesize. Must not be null, empty, and cannot exceed 10,000 characters.</param>
    /// <param name="speaker">Voice preset (enum) to use for synthesis.</param>
    /// <param name="outputPath">Path where the output WAV file will be saved.</param>
    /// <param name="language">Language code (default: "auto" for auto-detection).</param>
    /// <param name="instruct">Optional instruction prompt for voice style modification.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <exception cref="ArgumentNullException">Thrown when text is null.</exception>
    /// <exception cref="ArgumentException">Thrown when text is empty or exceeds 10,000 characters.</exception>
    public Task SynthesizeAsync(string text, QwenVoicePreset speaker, string outputPath,
                                string language = "auto", string? instruct = null,
                                IProgress<string>? progress = null)
        => SynthesizeAsync(text, speaker.ToSpeakerName(), outputPath, language, instruct, progress);

    /// <summary>
    /// Releases ONNX sessions and embedding tables held by tokenizer, embeddings, language model, and vocoder.
    /// </summary>
    public void Dispose()
    {
        _tokenizer.Dispose();
        _embeddings.Dispose();
        _languageModel.Dispose();
        _vocoder.Dispose();
    }
}
