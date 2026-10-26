namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Hardware-scaled capacity for <see cref="GenAiModelPool"/>, derived from measured GenAI
/// model footprints the same clamped style as the session-pool budgets in
/// <see cref="InferenceSessionPool.ScaleAcceleratorBudgetMb"/>: an entry count is only
/// raised when the admission budget alone could hold that many copies of the largest
/// measured model, so per-bucket memory admission stays the real OOM guard and this
/// count never forces an eviction the budget would not already allow.
/// </summary>
/// <remarks>
/// Measurement (2026-10-26, RTX 5070 12,227 MiB host): file sizes are the
/// <c>Content-Length</c> of the manifest-pinned downloads in
/// <c>bundled-models.manifest.json</c> (<c>download_file_sources</c> / source repo at the
/// pinned revision); the resident estimate is the pool's own
/// <see cref="GenAiModelKey.EstimateMemoryMb"/> formula (2x file bytes + 256 MiB, floor
/// 1,024 MiB):
/// <list type="table">
/// <item><description>Qwen2.5-1.5B-Instruct (text refinement, default variant): 5,916 MiB on disk (model.onnx.data 5,905 MiB) → 12,086 MiB estimated.</description></item>
/// <item><description>Phi-4-mini-instruct (GenAI translation, gpu-int4 variant): 3,276 MiB on disk → 6,808 MiB estimated.</description></item>
/// <item><description>Whisper GenAI ASR: tiny 224 MiB → 1,024 MiB (floor); small 1,078 MiB → 2,412 MiB estimated.</description></item>
/// </list>
/// On the 12 GB card the accelerator budget is 9,170 MiB (75% of 12,227) and the host
/// budget caps at 16,384 MiB, while the two largest estimates sum to 18,894 MiB — so
/// only one GenAI model fits with headroom and the scaled default stays 1 there. Two
/// entries require a budget of at least 24,172 MiB. See
/// <c>docs/reference/genai-model-pool-capacity.md</c>.
/// </remarks>
public static class GenAiModelPoolCapacity
{
    /// <summary>Optional entry-count override; a positive integer always wins over the scaled default.</summary>
    public const string MaxEntriesVariable = "TRACKDUB_GENAI_POOL_MAX_ENTRIES";

    /// <summary>Largest measured GenAI resident estimate (Qwen2.5-1.5B refinement), in MiB.</summary>
    public const long LargestMeasuredEstimateMb = 12086;

    /// <summary>Scaled capacity never exceeds the number of GenAI stages in a pipeline.</summary>
    public const int MaxScaledEntries = 3;

    public static int Resolve(
        long acceleratorBudgetMb,
        long hostMemoryBudgetMb,
        int? overrideEntries = null)
    {
        if (overrideEntries is > 0)
        {
            return overrideEntries.Value;
        }

        // A resident set lives in one admission bucket (accelerator or host), and
        // per-bucket admission remains the backstop for mixed sets.
        long budgetMb = Math.Max(acceleratorBudgetMb, hostMemoryBudgetMb);
        long entries = budgetMb / LargestMeasuredEstimateMb;
        return (int)Math.Clamp(entries, 1, MaxScaledEntries);
    }

    internal static int? ReadOverride() =>
        SharedPoolOptions.ParsePositiveInt32(Environment.GetEnvironmentVariable(MaxEntriesVariable));
}
