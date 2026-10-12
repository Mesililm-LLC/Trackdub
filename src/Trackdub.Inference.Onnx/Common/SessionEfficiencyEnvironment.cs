namespace Trackdub.Inference.Onnx.Common;

/// <summary>
/// Environment-driven efficiency knobs for ONNX Runtime session creation. Deployment opt-ins:
/// default behavior is unchanged unless the operator sets the variable, matching the MS
/// "Efficiency tips for Windows ML inference" memory-optimization guidance.
/// </summary>
internal static class SessionEfficiencyEnvironment
{
    /// <summary>
    /// Set to <c>1</c> to disable the ONNX Runtime CPU memory arena so allocations go to the
    /// system allocator. Trades throughput for lower peak memory (MS guidance for
    /// memory-footprint-sensitive hosts). Strictly <c>1</c>.
    /// </summary>
    public const string DisableCpuMemArenaVariable = "TRACKDUB_DISABLE_CPU_MEM_ARENA";

    internal static bool IsDisableCpuMemArenaEnabled(string? value) => value == "1";

    internal static bool DisableCpuMemArena =>
        IsDisableCpuMemArenaEnabled(Environment.GetEnvironmentVariable(DisableCpuMemArenaVariable));
}
