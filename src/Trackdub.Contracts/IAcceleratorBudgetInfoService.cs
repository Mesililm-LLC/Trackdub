namespace Trackdub.Contracts;

/// <summary>
/// How the inference session pool's accelerator (VRAM) admission budget was
/// resolved on this machine: detected GPU VRAM scaled by policy, an explicit
/// operator override, or the conservative floor when no VRAM was detected.
/// Surfaced by operator diagnostics (e.g. <c>trackdub doctor</c>) so a silent
/// floor fallback is visible instead of looking like a GPU failure.
/// </summary>
public sealed record AcceleratorBudgetInfo(
    long DetectedVramMb,
    long DefaultBudgetMb,
    long? OverrideBudgetMb)
{
    /// <summary>The budget admission actually uses: the override when set.</summary>
    public long EffectiveBudgetMb => OverrideBudgetMb ?? DefaultBudgetMb;

    /// <summary>Where <see cref="EffectiveBudgetMb"/> came from.</summary>
    public string Source => OverrideBudgetMb.HasValue
        ? "override (TRACKDUB_SESSION_VRAM_BUDGET_MB)"
        : DetectedVramMb > 0
            ? "detected VRAM"
            : "floor fallback (no GPU VRAM detected)";
}

/// <summary>Reports the resolved accelerator admission budget for diagnostics.</summary>
public interface IAcceleratorBudgetInfoService
{
    AcceleratorBudgetInfo GetBudgetInfo();
}
