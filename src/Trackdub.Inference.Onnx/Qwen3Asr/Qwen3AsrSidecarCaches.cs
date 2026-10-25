using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

/// <summary>
/// Process-wide shared <see cref="SidecarCache{T}"/> instances for Qwen3-ASR sidecar data.
/// </summary>
/// <remarks>
/// Both sidecars were previously loaded inside <c>TranscribeAsync</c>, so every transcription paid
/// a full read and decode of the fp16 embedding table (~300 MB on disk becoming a ~620 MB
/// <c>float[,]</c> on the large object heap) plus a tokenizer parse. Neither depends on the audio,
/// so caching them per model root removes that cost from every request after the first.
///
/// <para>Eviction: entries are never evicted automatically. If a model root changes at runtime
/// (hot-swap during development), call <see cref="Remove"/> to flush the stale entries.</para>
/// </remarks>
internal static class Qwen3AsrSidecarCaches
{
    /// <summary>Shared embedding-table cache keyed by the embed-tokens file path.</summary>
    public static readonly SidecarCache<Qwen3AsrEmbedTokens> EmbedTokens = new();

    /// <summary>Shared tokenizer cache keyed by model-root path.</summary>
    public static readonly SidecarCache<Qwen3AsrTokenizer> Tokenizers = new();

    /// <summary>Flushes both caches for <paramref name="modelRootPath"/>.</summary>
    public static void Remove(string modelRootPath, string embedTokensPath)
    {
        ArgumentNullException.ThrowIfNull(modelRootPath);
        ArgumentNullException.ThrowIfNull(embedTokensPath);
        Tokenizers.Remove(modelRootPath);
        EmbedTokens.Remove(embedTokensPath);
    }
}
