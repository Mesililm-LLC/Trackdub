using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Whisper;

/// <summary>
/// Process-wide shared <see cref="SidecarCache{T}"/> instances for Whisper sidecar data.
/// </summary>
/// <remarks>
/// The tokenizer (~52k vocab entries parsed from JSON) was loaded inside every transcription, and
/// the mel-bin count re-read <c>preprocessor_config.json</c> on each call. Neither depends on the
/// audio, so both are cached per model root.
///
/// <para>Eviction: entries are never evicted automatically. If a model root changes at runtime
/// (hot-swap during development), call <see cref="Remove"/> to flush the stale entries.</para>
/// </remarks>
internal static class WhisperSidecarCaches
{
    /// <summary>Shared tokenizer cache keyed by model-root path.</summary>
    public static readonly SidecarCache<WhisperTokenizerDecoder> Tokenizers = new();

    /// <summary>Shared mel-bin-count cache keyed by model-root path.</summary>
    public static readonly SidecarCache<int> MelBins = new();

    /// <summary>Flushes both caches for <paramref name="modelRootPath"/>.</summary>
    public static void Remove(string modelRootPath)
    {
        ArgumentNullException.ThrowIfNull(modelRootPath);
        Tokenizers.Remove(modelRootPath);
        MelBins.Remove(modelRootPath);
    }
}
