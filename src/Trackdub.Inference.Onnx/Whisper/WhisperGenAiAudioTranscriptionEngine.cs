using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Audio;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntimeGenAI;
using System.Text.Json;

namespace Trackdub.Inference.Onnx.Whisper;

/// <summary>
/// ONNX Runtime GenAI Whisper transcription engine.
/// Uses OGA's native generator loop instead of the manual token-by-token decoder.
/// </summary>
public sealed class WhisperGenAiAudioTranscriptionEngine : IAudioTranscriptionEngineAdapter, IStageRuntimeExecutionReporter
{
    public const string EngineFamilyName = "whisper-genai";

    private const int TargetSampleRate = 16000;
    private const double MaxClipDurationSeconds = 28;
    private const string GenAiConfigFileName = "genai_config.json";
    private const string AudioProcessorConfigFileName = "audio_processor_config.json";
    private const string WhisperLanguageDetectionPrompt = "<|startoftranscript|>";
    private const string WhisperProcessorPrompt = "<|startoftranscript|><|transcribe|><|notimestamps|>";
    private const double GenAiLanguageDetectionMaxLength = 8d;
    private const double GenAiMaxLength = 448d;
    private const double GenAiBeamCount = 5d;
    private const double GenAiRepetitionPenalty = 1.2d;

    private readonly IRuntimePlanner runtimePlanner;
    private readonly IRuntimePlanningPreferences? runtimePlanningPreferences;
    private readonly BenchmarkModelPathResolver modelPathResolver;
    private readonly WhisperOnnxAudioTranscriptionEngine legacyEngine;

    public WhisperGenAiAudioTranscriptionEngine(
        IRuntimePlanner runtimePlanner,
        BenchmarkModelPathResolver modelPathResolver,
        IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    {
        this.runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
        this.runtimePlanningPreferences = runtimePlanningPreferences;
        this.modelPathResolver = modelPathResolver ?? throw new ArgumentNullException(nameof(modelPathResolver));
        legacyEngine = new WhisperOnnxAudioTranscriptionEngine(runtimePlanner, modelPathResolver);
    }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public string EngineFamily => EngineFamilyName;

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken) =>
        await TranscribeAsync(
            new AudioTranscriptionRequest(normalizedAudioPath, regions),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(new StageRuntimePlanningRequest(
                RuntimeStage.Asr,
                options.NormalizedPreferredModelAlias,
                SourceLanguage: request.SourceLanguage,
                RequirePreferredModelAlias: options.RequirePreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return await TranscribeAsync(request, plan, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.NormalizedAudioPath);
        ArgumentNullException.ThrowIfNull(request.Regions);
        EnsurePlanReady(plan);

        if (!IsGenAiAlias(plan.ModelAlias))
        {
            IReadOnlyList<RecognizedTranscriptSegment> legacySegments = await legacyEngine.TranscribeAsync(
                request,
                plan,
                cancellationToken).ConfigureAwait(false);
            LastExecutionSummary = legacyEngine.LastExecutionSummary;
            return legacySegments;
        }

        string modelRootPath = ResolveModelRootPath(plan);
        EnsureGenAiModelRoot(modelRootPath);
        IReadOnlyDictionary<int, string> languageTokensById =
            await LoadLanguageTokenIdsAsync(modelRootPath).ConfigureAwait(false);

        // Read audio before the zero-region check so we can compute duration and apply the
        // same short-audio fallback as WhisperOnnxAudioTranscriptionEngine: treat the full
        // clip as a single region when VAD found nothing on audio shorter than 30 seconds.
        // CreateResampledStream returns the source as-is when sample rates match; otherwise
        // it wraps and takes ownership of it. Dispose only the outer reader.
        //
        // NOTE: loading full PCM here to get duration is slightly wasteful for the long-audio
        // early-exit branch (≥ 30s + no regions). A WAV-header-only duration probe would avoid
        // reading sample data in that case, but would require a header-level reader that does not
        // yet exist in this layer. The current approach is acceptable because long-audio + no-VAD
        // is expected to be rare in practice and the I/O cost is bounded by the file size.
        // TODO: once AudioArtifactValidator's WAV header parser is extracted to a shared assembly,
        // use it here to read only the fmt/data chunk metadata, then load PCM only when
        // transcription will actually proceed.
        IAudioSamples rawAudio = await WaveAudioReader.ReadMonoPcm16Async(request.NormalizedAudioPath, cancellationToken).ConfigureAwait(false);
        using IAudioSamples targetAudio = AudioResampler.CreateResampledStream(rawAudio, TargetSampleRate);
        double durationSeconds = targetAudio.SampleFrameCount / (double)TargetSampleRate;

        IReadOnlyList<SpeechRegion> effectiveRegions = request.Regions;
        if (request.Regions.Count == 0)
        {
            if (durationSeconds >= 30.0)
            {
                LastExecutionSummary = CreateExecutionSummary(plan,
                    $"ASR skipped: VAD detected no speech regions in {durationSeconds:F1}s audio.");
                return [];
            }

            // Sub-100ms audio cannot produce a transcription: TranscribeRegionAsync skips
            // all chunks whose duration is below the 0.1 s threshold, returning empty text.
            if (durationSeconds < 0.1)
            {
                LastExecutionSummary = CreateExecutionSummary(plan,
                    $"ASR skipped: audio duration {durationSeconds * 1000:F0}ms is below the 100ms minimum chunk threshold; treating as silent.");
                return [];
            }

            // Short audio with no VAD regions — treat full clip as one region.
            effectiveRegions = [new SpeechRegion(0, 0.0, durationSeconds)];
        }

        var segments = new List<RecognizedTranscriptSegment>(effectiveRegions.Count);
        int droppedDegenerateChunks = 0;
        {
            GenAiModelKey modelKey = await GenAiModelKey.CreateAsync(
                modelRootPath, plan.ExecutionProvider!.Value, plan.ModelId, plan.Variant, plan.DeviceIndex,
                plan.ModelRevisionHash, cancellationToken).ConfigureAwait(false);
            using GenAiModelLease modelLease = await GenAiModelPool.Shared
                .GetLeaseAsync(modelKey, cancellationToken).ConfigureAwait(false);
            using IDisposable? executionAdmission = await CpuExecutionAdmission.Shared
                .AcquireAsync(modelKey.Provider, cancellationToken).ConfigureAwait(false);
            Model model = modelLease.Model;
            using MultiModalProcessor processor = new(model);
            // Short VAD fragments are unreliable language probes. Match the legacy Whisper
            // engine: vote on merged context, but keep the caller's transcription regions and
            // indices intact for speaker assignment and selective retranscription.
            string? transcriptionLanguage = NormalizeLanguageCode(request.SourceLanguage)
                ?? DetectTranscriptLanguage(model, processor, targetAudio, effectiveRegions,
                    durationSeconds, languageTokensById, cancellationToken);
            // targetAudio is already loaded above.
            foreach (SpeechRegion region in effectiveRegions.OrderBy(static region => region.Index))
            {
                cancellationToken.ThrowIfCancellationRequested();

                RegionTranscription transcription = TranscribeRegion(
                    model,
                    processor,
                    targetAudio,
                    region,
                    durationSeconds,
                    transcriptionLanguage,
                    languageTokensById,
                    cancellationToken);

                droppedDegenerateChunks += transcription.DroppedDegenerateChunks;

                if (IsDegenerateTranscriptText(transcription.Text))
                {
                    continue;
                }

                segments.Add(new RecognizedTranscriptSegment(
                    region.Index,
                    region.StartSeconds,
                    region.EndSeconds,
                    transcription.Text,
                    transcription.DetectedLanguage,
                    transcription.Words));
            }
        }

        string bootstrapDetail = "ONNX Runtime GenAI native generator loop.";
        if (droppedDegenerateChunks > 0)
        {
            bootstrapDetail +=
                $" Dropped {droppedDegenerateChunks} degenerate chunk(s) (punctuation-only / period hallucination).";
        }

        LastExecutionSummary = CreateExecutionSummary(plan, bootstrapDetail);
        return segments;
    }

    internal static string CleanDecodedText(string decodedText)
    {
        if (string.IsNullOrWhiteSpace(decodedText))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(decodedText.Length);
        for (int index = 0; index < decodedText.Length;)
        {
            if (decodedText.AsSpan(index).StartsWith("<|", StringComparison.Ordinal))
            {
                int end = decodedText.IndexOf("|>", index, StringComparison.Ordinal);
                if (end >= 0)
                {
                    index = end + 2;
                    continue;
                }
            }

            builder.Append(decodedText[index]);
            index++;
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// True when decoded ASR text carries no alphanumeric content — Whisper's typical
    /// punctuation-only hallucination (". . .", "。 。 。", "S S S") on bad features or non-speech.
    /// </summary>
    internal static bool IsDegenerateTranscriptText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static string GetAudioProcessorPromptForTesting() => WhisperProcessorPrompt;

    internal static string GetLanguageDetectionPromptForTesting() => WhisperLanguageDetectionPrompt;

    internal static string BuildTranscriptionPromptForTesting(string? detectedLanguage) =>
        BuildTranscriptionPrompt(detectedLanguage);

    internal static string? TryInferDetectedLanguage(string decodedText)
    {
        if (string.IsNullOrWhiteSpace(decodedText))
        {
            return null;
        }

        int searchIndex = 0;
        while (searchIndex < decodedText.Length)
        {
            int start = decodedText.IndexOf("<|", searchIndex, StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            int end = decodedText.IndexOf("|>", start, StringComparison.Ordinal);
            if (end < 0)
            {
                return null;
            }

            string token = decodedText[(start + 2)..end].Trim().ToLowerInvariant();
            if (IsLanguageToken(token))
            {
                return token;
            }

            searchIndex = end + 2;
        }

        return null;
    }

    private static void EnsurePlanReady(StageRuntimePlan plan)
    {
        if (plan.IsRunnable() && plan.ExecutionProvider is not null && !string.IsNullOrWhiteSpace(plan.ModelAlias))
        {
            return;
        }

        throw new InvalidOperationException(
            plan.Fallback?.Detail ?? "Runtime planner did not produce a ready ASR plan.");
    }

    private static bool IsGenAiAlias(string? modelAlias) =>
        !string.IsNullOrWhiteSpace(modelAlias) &&
        modelAlias.Contains("genai", StringComparison.OrdinalIgnoreCase);

    private static void EnsureGenAiModelRoot(string modelRootPath)
    {
        string configPath = Path.Join(modelRootPath, GenAiConfigFileName);
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("Whisper GenAI model root does not contain genai_config.json.", configPath);
        }

        string audioProcessorConfigPath = Path.Join(modelRootPath, AudioProcessorConfigFileName);
        if (!File.Exists(audioProcessorConfigPath))
        {
            throw new FileNotFoundException("Whisper GenAI model root does not contain audio_processor_config.json.", audioProcessorConfigPath);
        }
    }

    private static bool IsLanguageToken(string token)
    {
        if (token is "startoftranscript" or "startofprev" or "transcribe" or "translate" or
            "notimestamps" or "nospeech" or "nocaptions" or "prev" or "endoftext" or "endoftranscript")
        {
            return false;
        }

        return token.Length is >= 2 and <= 8 &&
               token.All(static character => character is >= 'a' and <= 'z' || character == '-');
    }

    private RegionTranscription TranscribeRegion(
        Model model,
        MultiModalProcessor processor,
        IAudioSamples targetAudio,
        SpeechRegion region,
        double durationSeconds,
        string? sourceLanguage,
        IReadOnlyDictionary<int, string> languageTokensById,
        CancellationToken cancellationToken)
    {
        double startSeconds = Math.Clamp(region.StartSeconds, 0d, durationSeconds);
        double endSeconds = Math.Clamp(region.EndSeconds, 0d, durationSeconds);
        if (endSeconds <= startSeconds)
        {
            return new RegionTranscription(string.Empty, DetectedLanguage: null, Words: []);
        }

        var chunkTexts = new List<string>();
        var chunkWords = new List<RecognizedTranscriptWord>();
        var detectedLanguages = new List<string>();
        int chunkIndex = 0;
        int droppedDegenerateChunks = 0;

        for (double chunkStartSeconds = startSeconds;
             chunkStartSeconds < endSeconds;
             chunkStartSeconds += MaxClipDurationSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            double chunkEndSeconds = Math.Min(endSeconds, chunkStartSeconds + MaxClipDurationSeconds);
            if (chunkEndSeconds - chunkStartSeconds < 0.1)
            {
                continue;
            }

            byte[] clipBytes = EncodeClip(targetAudio, chunkStartSeconds, chunkEndSeconds, cancellationToken);

            string? detectedLanguage = NormalizeLanguageCode(sourceLanguage)
                ?? DetectClipLanguage(model, processor, clipBytes, languageTokensById, cancellationToken);
            int[] transcriptionTokens = GenerateClipTokens(
                model,
                processor,
                clipBytes,
                BuildTranscriptionPrompt(detectedLanguage),
                GenAiMaxLength,
                cancellationToken);
            string decodedText = processor.Decode(transcriptionTokens);
            string cleanedText = CleanDecodedText(decodedText);
            detectedLanguage = NormalizeLanguageCode(sourceLanguage)
                ?? InferLanguageFromTokenIds(transcriptionTokens, languageTokensById)
                ?? TryInferDetectedLanguage(decodedText)
                ?? detectedLanguage;
            if (IsDegenerateTranscriptText(cleanedText))
            {
                // Whisper often hallucinates ".", "。", or mixed punctuation on
                // non-speech / bad features. Dropping the chunk keeps those out of
                // the transcript instead of polishing garbage downstream.
                droppedDegenerateChunks++;
            }
            else if (!string.IsNullOrWhiteSpace(cleanedText))
            {
                chunkTexts.Add(cleanedText);
                chunkWords.AddRange(WhisperOnnxAudioTranscriptionEngine.BuildRecognizedWords(
                    cleanedText,
                    chunkStartSeconds,
                    chunkEndSeconds,
                    []));
            }

            if (!string.IsNullOrWhiteSpace(detectedLanguage))
            {
                detectedLanguages.Add(detectedLanguage);
            }

            chunkIndex++;
        }

        return new RegionTranscription(
            string.Join(" ", chunkTexts).Trim(),
            ResolveDetectedLanguage(detectedLanguages),
            WhisperOnnxAudioTranscriptionEngine.ReindexWords(chunkWords),
            droppedDegenerateChunks);
    }

    // One chunk is at most ~28 s of 16 kHz mono PCM16 (~0.9 MB), so the encoded WAV is
    // bounded and stays entirely in memory for Audios.Load.
    internal static byte[] EncodeClip(
        IAudioSamples targetAudio,
        double startSeconds,
        double endSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetAudio);
        cancellationToken.ThrowIfCancellationRequested();

        long startSample = Math.Max(0, (long)Math.Floor(startSeconds * TargetSampleRate));
        long endSample = Math.Min(targetAudio.SampleFrameCount, (long)Math.Ceiling(endSeconds * TargetSampleRate));
        if (endSample <= startSample)
        {
            return WaveAudioWriter.EncodeMonoPcm16([], TargetSampleRate);
        }

        cancellationToken.ThrowIfCancellationRequested();
        float[] samples = new float[checked((int)(endSample - startSample))];
        targetAudio.ReadMonoSamples(startSample, samples);
        cancellationToken.ThrowIfCancellationRequested();
        return WaveAudioWriter.EncodeMonoPcm16(samples, TargetSampleRate);
    }

    private static string? DetectTranscriptLanguage(
        Model model,
        MultiModalProcessor processor,
        IAudioSamples targetAudio,
        IReadOnlyList<SpeechRegion> regions,
        double durationSeconds,
        IReadOnlyDictionary<int, string> languageTokensById,
        CancellationToken cancellationToken)
    {
        var languages = new List<string>();
        foreach (SpeechRegion region in WhisperOnnxAudioTranscriptionEngine.BuildTranscriptionRegions(regions, durationSeconds))
        {
            cancellationToken.ThrowIfCancellationRequested();
            double endSeconds = Math.Min(region.EndSeconds, region.StartSeconds + MaxClipDurationSeconds);
            if (endSeconds - region.StartSeconds < 0.1)
            {
                continue;
            }

            byte[] clipBytes = EncodeClip(targetAudio, region.StartSeconds, endSeconds, cancellationToken);
            string? language = DetectClipLanguage(model, processor, clipBytes, languageTokensById, cancellationToken);
            if (!string.IsNullOrWhiteSpace(language))
            {
                languages.Add(language);
            }
        }

        return ResolveDetectedLanguage(languages);
    }

    private static string? DetectClipLanguage(
        Model model,
        MultiModalProcessor processor,
        byte[] clipBytes,
        IReadOnlyDictionary<int, string> languageTokensById,
        CancellationToken cancellationToken)
    {
        // Whisper predicts the spoken language as a dedicated token (e.g. <|es|>) right after
        // <|startoftranscript|>. Read it from the raw token-id sequence rather than the decoded
        // string: ONNX Runtime GenAI's Whisper processor strips special tokens when decoding to
        // text, so the <|xx|> marker never survives into the string and a text scan always
        // returns null. Token ids are stable regardless of how text is rendered.
        int[] tokens = GenerateClipTokens(
            model,
            processor,
            clipBytes,
            WhisperLanguageDetectionPrompt,
            GenAiLanguageDetectionMaxLength,
            cancellationToken);
        return InferLanguageFromTokenIds(tokens, languageTokensById)
            ?? TryInferDetectedLanguage(processor.Decode(tokens));
    }

    private static int[] GenerateClipTokens(
        Model model,
        MultiModalProcessor processor,
        byte[] clipBytes,
        string prompt,
        double maxLength,
        CancellationToken cancellationToken)
    {
        using Audios audios = Audios.Load(clipBytes);
        using NamedTensors inputTensors = processor.ProcessAudios([prompt], audios);

        using GeneratorParams generatorParams = new(model);

        // Stabilize output and prevent "las que las que..." hallucination loops
        generatorParams.SetSearchOption("max_length", maxLength);
        generatorParams.SetSearchOption("num_beams", GenAiBeamCount);
        generatorParams.SetSearchOption("repetition_penalty", GenAiRepetitionPenalty);

        using Generator generator = new(model, generatorParams);
        generator.SetInputs(inputTensors);

        while (!generator.IsDone())
        {
            cancellationToken.ThrowIfCancellationRequested();
            generator.GenerateNextToken();
        }

        return generator.GetSequence(0).ToArray();
    }

    /// <summary>
    /// Representative warmup profile for the shared Whisper GenAI model: runs the real
    /// audio preprocessing plus a bounded generator pass on 0.1s of silence. No artifacts,
    /// stage-run records, or readiness state are produced.
    /// </summary>
    internal static async Task WarmModelAsync(Model model, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(model);

        byte[] silenceWav = WaveAudioWriter.EncodeMonoPcm16(new float[1600], TargetSampleRate);
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using MultiModalProcessor processor = new(model);
            _ = GenerateClipTokens(
                model, processor, silenceWav, WhisperLanguageDetectionPrompt, 8, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    internal static string? InferLanguageFromTokenIds(
        IReadOnlyList<int> tokenIds,
        IReadOnlyDictionary<int, string> languageTokensById)
    {
        foreach (int tokenId in tokenIds)
        {
            if (languageTokensById.TryGetValue(tokenId, out string? languageCode))
            {
                return languageCode;
            }
        }

        return null;
    }

    // Builds a {token-id -> language-code} map (e.g. 50259 -> "en") from the model's
    // tokenizer.json added_tokens. Whisper GenAI models ship tokenizer.json as the only file
    // carrying the id <-> "<|xx|>" mapping (there is no vocab.json), so WhisperTokenizerDecoder
    // cannot be reused. Returns an empty map when the file or section is absent; detection then
    // degrades to text scanning.
    internal static async Task<IReadOnlyDictionary<int, string>> LoadLanguageTokenIdsAsync(string modelRootPath)
    {
        var languageTokensById = new Dictionary<int, string>();
        string tokenizerPath = Path.Join(modelRootPath, "tokenizer.json");
        if (!File.Exists(tokenizerPath))
        {
            return languageTokensById;
        }

        await using FileStream stream = File.OpenRead(tokenizerPath);
        using JsonDocument document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("added_tokens", out JsonElement addedTokens) ||
            addedTokens.ValueKind is not JsonValueKind.Array)
        {
            return languageTokensById;
        }

        foreach (JsonElement token in addedTokens.EnumerateArray())
        {
            if (!token.TryGetProperty("id", out JsonElement idElement) ||
                !idElement.TryGetInt32(out int tokenId) ||
                !token.TryGetProperty("content", out JsonElement contentElement) ||
                contentElement.GetString() is not string content ||
                !content.StartsWith("<|", StringComparison.Ordinal) ||
                !content.EndsWith("|>", StringComparison.Ordinal))
            {
                continue;
            }

            string inner = content[2..^2];
            if (IsLanguageToken(inner))
            {
                languageTokensById[tokenId] = inner;
            }
        }

        return languageTokensById;
    }

    private static string BuildTranscriptionPrompt(string? detectedLanguage)
    {
        string? normalizedLanguage = NormalizeLanguageCode(detectedLanguage);
        return normalizedLanguage is null
            ? WhisperProcessorPrompt
            : $"<|startoftranscript|><|{normalizedLanguage}|><|transcribe|><|notimestamps|>";
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string normalized = languageCode.Trim().ToLowerInvariant();
        return IsLanguageToken(normalized)
            ? normalized
            : null;
    }

    private static string? ResolveDetectedLanguage(IReadOnlyList<string> detectedLanguages) =>
        detectedLanguages
            .Where(static language => !string.IsNullOrWhiteSpace(language))
            .Select(static language => language.Trim().ToLowerInvariant())
            .GroupBy(static language => language, StringComparer.Ordinal)
            .OrderByDescending(static group => group.Count())
            .ThenBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => group.Key)
            .FirstOrDefault();

    private string ResolveModelRootPath(StageRuntimePlan plan)
    {
        return PlannedRuntimeModelResolver.ResolveModelRootPath(plan, modelPathResolver);
    }

    private static StageRuntimeExecutionSummary CreateExecutionSummary(
        StageRuntimePlan plan,
        string bootstrapDetail) =>
        new(
            "auto",
            plan.ExecutionProvider!.Value.ToString().ToLowerInvariant(),
            plan.ModelId,
            plan.ModelAlias,
            plan.Variant,
            plan.Fallback is null ? bootstrapDetail : $"{bootstrapDetail} {plan.Fallback.Detail}");

    private sealed record RegionTranscription(
        string Text,
        string? DetectedLanguage,
        IReadOnlyList<RecognizedTranscriptWord> Words,
        int DroppedDegenerateChunks = 0);
}
