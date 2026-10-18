using Trackdub.Contracts;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Reports how the shared pool's accelerator admission budget resolved:
/// VRAM detected on this machine (DXGI on Windows; /proc + sysfs on Linux,
/// with an NVML fallback for containers where /proc/driver/nvidia is hidden),
/// scaled by <see cref="InferenceSessionPool.ScaleAcceleratorBudgetMb"/>, or
/// the explicit TRACKDUB_SESSION_VRAM_BUDGET_MB override when set.
/// </summary>
public sealed class AcceleratorBudgetInfoService : IAcceleratorBudgetInfoService
{
    public AcceleratorBudgetInfo GetBudgetInfo() => new(
        DetectedVramMb: InferenceSessionPool.DetectMaxAcceleratorVramMb(),
        DefaultBudgetMb: InferenceSessionPool.DefaultMemoryBudgetMb,
        OverrideBudgetMb: SharedPoolOptions.ReadPositiveInt64(SharedPoolOptions.BudgetMbVariable));
}
