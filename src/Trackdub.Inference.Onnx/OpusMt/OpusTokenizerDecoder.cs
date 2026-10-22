using System.Text.Json;
using Microsoft.ML.Tokenizers;

namespace Trackdub.Inference.Onnx.OpusMt;

internal sealed class OpusTokenizerDecoder
{
    private readonly SentencePieceTokenizer sourceTokenizer;
    private readonly SentencePieceTokenizer targetTokenizer;
    private readonly IReadOnlyDictionary<string, int> modelIdByPiece;
    private readonly IReadOnlyDictionary<int, string> pieceByModelId;
    private readonly IReadOnlyDictionary<int, string> sourcePieceByTokenizerId;
    private readonly IReadOnlyDictionary<string, int> targetTokenizerIdByPiece;

    private OpusTokenizerDecoder(
        SentencePieceTokenizer sourceTokenizer,
        SentencePieceTokenizer targetTokenizer,
        IReadOnlyDictionary<string, int> modelIdByPiece,
        IReadOnlyDictionary<int, string> pieceByModelId,
        IReadOnlyDictionary<int, string> sourcePieceByTokenizerId,
        IReadOnlyDictionary<string, int> targetTokenizerIdByPiece,
        int decoderStartTokenId,
        int endOfSentenceTokenId,
        int padTokenId,
        int maxGenerationLength,
        bool configFilePresent,
        bool generationConfigFilePresent)
    {
        this.sourceTokenizer = sourceTokenizer;
        this.targetTokenizer = targetTokenizer;
        this.modelIdByPiece = modelIdByPiece;
        this.pieceByModelId = pieceByModelId;
        this.sourcePieceByTokenizerId = sourcePieceByTokenizerId;
        this.targetTokenizerIdByPiece = targetTokenizerIdByPiece;
        DecoderStartTokenId = decoderStartTokenId;
        EndOfSentenceTokenId = endOfSentenceTokenId;
        PadTokenId = padTokenId;
        MaxGenerationLength = maxGenerationLength;
        ConfigFilePresent = configFilePresent;
        GenerationConfigFilePresent = generationConfigFilePresent;
        VocabularySize = modelIdByPiece.Count;
        RequiresTargetLanguagePrefix = modelIdByPiece.Keys
            .Any(static piece => piece.StartsWith(">>", StringComparison.Ordinal) &&
                                 piece.EndsWith("<<", StringComparison.Ordinal));
    }

    public int DecoderStartTokenId { get; }

    public int EndOfSentenceTokenId { get; }

    public int PadTokenId { get; }

    public int MaxGenerationLength { get; }

    /// <summary>
    /// True when <c>config.json</c> was present in the model root at load time. The desktop model
    /// cache only guarantees the manifest's ONNX/tokenizer files, so when this is false the special
    /// token ids are derived from the vocabulary instead of the model config.
    /// </summary>
    public bool ConfigFilePresent { get; }

    /// <summary>
    /// True when <c>generation_config.json</c> was present in the model root at load time.
    /// </summary>
    public bool GenerationConfigFilePresent { get; }

    /// <summary>
    /// Number of Marian vocabulary entries loaded from <c>vocab.json</c>. Fallback special token
    /// ids are derived from this count.
    /// </summary>
    public int VocabularySize { get; }

    public static async Task<OpusTokenizerDecoder> LoadAsync(string modelRootPath)
    {
        string sourceTokenizerPath = ResolveExistingPath(modelRootPath, "source.spm", "source.model");
        string targetTokenizerPath = ResolveExistingPath(modelRootPath, "target.spm", "target.model");
        string vocabPath = Path.Join(modelRootPath, "vocab.json");
        string configPath = Path.Join(modelRootPath, "config.json");
        string generationConfigPath = Path.Join(modelRootPath, "generation_config.json");

        if (!File.Exists(vocabPath))
        {
            throw new FileNotFoundException("The Opus vocabulary mapping was not found.", vocabPath);
        }

        IReadOnlyDictionary<string, int> modelIdByPiece = await LoadVocabularyAsync(vocabPath).ConfigureAwait(false);
        OpusTokenizerConfig config = await LoadConfigAsync(configPath, generationConfigPath, modelIdByPiece.Count).ConfigureAwait(false);
        using FileStream sourceStream = File.OpenRead(sourceTokenizerPath);
        using FileStream targetStream = File.OpenRead(targetTokenizerPath);

        SentencePieceTokenizer sourceTokenizer = SentencePieceTokenizer.Create(
            sourceStream,
            addBeginningOfSentence: false,
            addEndOfSentence: true);
        SentencePieceTokenizer targetTokenizer = SentencePieceTokenizer.Create(
            targetStream,
            addBeginningOfSentence: false,
            addEndOfSentence: false);
        IReadOnlyDictionary<int, string> pieceByModelId = modelIdByPiece
            .ToDictionary(pair => pair.Value, pair => pair.Key);
        IReadOnlyDictionary<int, string> sourcePieceByTokenizerId = sourceTokenizer.Vocabulary
            .ToDictionary(pair => pair.Value, pair => pair.Key);

        return new OpusTokenizerDecoder(
            sourceTokenizer,
            targetTokenizer,
            modelIdByPiece,
            pieceByModelId,
            sourcePieceByTokenizerId,
            targetTokenizer.Vocabulary,
            config.DecoderStartTokenId,
            config.EndOfSentenceTokenId,
            config.PadTokenId,
            config.MaxGenerationLength,
            config.ConfigFilePresent,
            config.GenerationConfigFilePresent);
    }

    public long[] EncodeSourceText(string text, string? targetLanguagePrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        IEnumerable<long> encoded = sourceTokenizer
            .EncodeToIds(text.Trim())
            .Select(MapSourceTokenizerIdToModelId)
            .Select(static tokenId => (long)tokenId);

        if (targetLanguagePrefix is not null &&
            modelIdByPiece.TryGetValue(targetLanguagePrefix, out int prefixModelId))
        {
            encoded = new long[] { (long)prefixModelId }.Concat(encoded);
        }

        return encoded.ToArray();
    }

    /// <summary>
    /// Returns the Marian target-language prefix piece (e.g. <c>&gt;&gt;pt&lt;&lt;</c>)
    /// for <paramref name="isoLanguageCode"/> if the model vocabulary contains it,
    /// or <see langword="null"/> if no prefix piece is defined. The returned piece is
    /// resolved to a token id at encode time by <see cref="EncodeSourceText"/>.
    /// </summary>
    public string? ResolveTargetLanguagePrefix(string isoLanguageCode, string? catalogPrefix = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(isoLanguageCode);
        // A verified catalog may map ISO-639-1 targets to the model's ISO-639-3 token.
        // Only an exact vocabulary member can be used; never synthesize an unverified token.
        string prefix = catalogPrefix ?? $">>{isoLanguageCode}<<";
        return modelIdByPiece.ContainsKey(prefix) ? prefix : null;
    }

    /// <summary>
    /// True when the model's vocabulary contains any <c>&gt;&gt;xx&lt;&lt;</c> language
    /// prefix piece, indicating it is a multi-target Marian model (e.g.
    /// <c>opus-mt-en-ROMANCE</c>) that needs a target-language prefix to disambiguate
    /// translations. False for single-pair models.
    /// </summary>
    public bool RequiresTargetLanguagePrefix { get; }

    public string DecodeTargetText(IEnumerable<long> tokenIds)
    {
        int[] targetTokenizerIds = tokenIds
            .Where(tokenId => tokenId != DecoderStartTokenId &&
                              tokenId != EndOfSentenceTokenId &&
                              tokenId != PadTokenId)
            .Select(MapModelIdToTargetTokenizerId)
            .ToArray();
        return targetTokenizer.Decode(targetTokenizerIds).Trim();
    }

    private int MapSourceTokenizerIdToModelId(int tokenizerId)
    {
        if (!sourcePieceByTokenizerId.TryGetValue(tokenizerId, out string? piece))
        {
            throw new InvalidOperationException($"Source tokenizer piece id '{tokenizerId}' did not resolve to a Marian token piece.");
        }

        if (modelIdByPiece.TryGetValue(piece, out int modelId))
        {
            return modelId;
        }

        if (modelIdByPiece.TryGetValue(sourceTokenizer.UnknownToken, out int unknownId))
        {
            return unknownId;
        }

        throw new InvalidOperationException($"Marian vocabulary did not define a model id for token piece '{piece}'.");
    }

    private int MapModelIdToTargetTokenizerId(long modelTokenId)
    {
        int checkedModelTokenId = checked((int)modelTokenId);
        if (!pieceByModelId.TryGetValue(checkedModelTokenId, out string? piece))
        {
            throw new InvalidOperationException($"Marian vocabulary did not define token piece for model id '{checkedModelTokenId}'.");
        }

        if (targetTokenizerIdByPiece.TryGetValue(piece, out int tokenizerId))
        {
            return tokenizerId;
        }

        if (targetTokenizerIdByPiece.TryGetValue(targetTokenizer.UnknownToken, out int unknownId))
        {
            return unknownId;
        }

        throw new InvalidOperationException($"Target tokenizer did not define an id for Marian token piece '{piece}'.");
    }

    /// <summary>
    /// Resolves Marian special token ids. Keys missing from <c>config.json</c> fall back to the
    /// Helsinki convention (<c>decoder_start_token_id == pad_token_id == vocab_size - 1</c>,
    /// <c>eos_token_id == 0</c>) instead of the legacy hardcoded 65000, which is out of bounds
    /// for models with smaller vocabularies (e.g. opus-mt-en-fr, vocab 59514) and surfaces as an
    /// opaque ONNX <c>Gather</c> out-of-bounds failure on the first decoder step. The desktop model
    /// cache only guarantees the ONNX/spm/vocab files, so <c>config.json</c> is often absent.
    /// </summary>
    internal static async Task<OpusTokenizerConfig> LoadConfigAsync(string configPath, string generationConfigPath, int vocabularySize)
    {
        int fallbackSpecialTokenId = Math.Max(0, vocabularySize - 1);
        int? decoderStartTokenId = null;
        int? endOfSentenceTokenId = null;
        int? padTokenId = null;
        int maxGenerationLength = 256;
        bool configFilePresent = File.Exists(configPath);
        bool generationConfigFilePresent = File.Exists(generationConfigPath);

        if (configFilePresent)
        {
            string configText = await File.ReadAllTextAsync(configPath).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(configText);
            JsonElement root = document.RootElement;
            decoderStartTokenId = ReadInt32(root, "decoder_start_token_id");
            endOfSentenceTokenId = ReadInt32(root, "eos_token_id");
            padTokenId = ReadInt32(root, "pad_token_id");
            maxGenerationLength = ReadInt32(root, "max_position_embeddings") ?? maxGenerationLength;
        }

        if (generationConfigFilePresent)
        {
            string genConfigText = await File.ReadAllTextAsync(generationConfigPath).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(genConfigText);
            JsonElement root = document.RootElement;
            maxGenerationLength = ReadInt32(root, "max_length") ?? maxGenerationLength;
        }

        int resolvedDecoderStartTokenId = decoderStartTokenId ?? fallbackSpecialTokenId;
        int resolvedEndOfSentenceTokenId = endOfSentenceTokenId ?? 0;
        int resolvedPadTokenId = padTokenId ?? fallbackSpecialTokenId;
        ValidateTokenId(resolvedDecoderStartTokenId, nameof(resolvedDecoderStartTokenId), vocabularySize, configPath);
        ValidateTokenId(resolvedEndOfSentenceTokenId, nameof(resolvedEndOfSentenceTokenId), vocabularySize, configPath);
        ValidateTokenId(resolvedPadTokenId, nameof(resolvedPadTokenId), vocabularySize, configPath);

        return new OpusTokenizerConfig(
            resolvedDecoderStartTokenId,
            resolvedEndOfSentenceTokenId,
            resolvedPadTokenId,
            Math.Max(32, maxGenerationLength),
            configFilePresent,
            generationConfigFilePresent);
    }

    private static int? ReadInt32(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement element))
        {
            return null;
        }

        return element.ValueKind is JsonValueKind.Number && element.TryGetInt32(out int value)
            ? value
            : null;
    }

    private static void ValidateTokenId(int tokenId, string tokenName, int vocabularySize, string configPath)
    {
        if (tokenId < 0 || tokenId >= vocabularySize)
        {
            throw new InvalidOperationException(
                $"Opus {tokenName} '{tokenId}' is outside the model vocabulary (size {vocabularySize}). " +
                $"Check 'decoder_start_token_id'/'eos_token_id'/'pad_token_id' in '{configPath}'.");
        }
    }

    private static async Task<IReadOnlyDictionary<string, int>> LoadVocabularyAsync(string vocabPath)
    {
        string vocabText = await File.ReadAllTextAsync(vocabPath).ConfigureAwait(false);
        Dictionary<string, int>? vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(vocabText);
        if (vocabulary is null || vocabulary.Count == 0)
        {
            throw new InvalidOperationException($"The Opus Marian vocabulary at '{vocabPath}' was empty or invalid.");
        }

        return vocabulary;
    }

    private static string ResolveExistingPath(string modelRootPath, params string[] fileNames)
    {
        foreach (string fileName in fileNames)
        {
            string candidatePath = Path.Join(modelRootPath, fileName);
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new FileNotFoundException(
            $"The Opus tokenizer was not found under '{modelRootPath}'.",
            Path.Join(modelRootPath, fileNames[0]));
    }

    internal sealed record OpusTokenizerConfig(
        int DecoderStartTokenId,
        int EndOfSentenceTokenId,
        int PadTokenId,
        int MaxGenerationLength,
        bool ConfigFilePresent,
        bool GenerationConfigFilePresent);
}
