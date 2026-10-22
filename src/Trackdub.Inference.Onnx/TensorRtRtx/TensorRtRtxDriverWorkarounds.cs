using System.Globalization;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// TensorRT-RTX 1.7 known issue: on Windows with NVIDIA driver R615 or earlier, TensorRT-RTX inference
/// launched after a DirectML RecordDispatch in the same process fails with CUDA_ERROR_INVALID_VALUE.
/// Trackdub mixes DirectML and TensorRT-RTX stages in one process, so affected drivers get NVIDIA's
/// workaround: cap the builder's tactic shared memory at 99 KiB. The cap costs kernel choices, so
/// newer drivers never get it.
/// </summary>
internal static class TensorRtRtxDriverWorkarounds
{
    internal const string MaxSharedMemSizeOptionKey = "nv_max_shared_mem_size";

    internal const int DirectMlInteropLastAffectedDriverBranch = 615;

    // Bytes; the EP maps nv_max_shared_mem_size to MemoryPoolType::kTACTIC_SHARED_MEMORY.
    internal const string DirectMlInteropSharedMemoryLimitBytes = "101376";

    internal static void Apply(IDictionary<string, string> trtOptions, string? driverVersion, bool isWindows)
    {
        if (!isWindows ||
            trtOptions.ContainsKey(MaxSharedMemSizeOptionKey) ||
            !IsDirectMlInteropAffected(driverVersion))
        {
            return;
        }

        trtOptions[MaxSharedMemSizeOptionKey] = DirectMlInteropSharedMemoryLimitBytes;
    }

    internal static bool IsDirectMlInteropAffected(string? driverVersion) =>
        TryParseNvidiaDriverBranch(driverVersion, out int branch) &&
        branch <= DirectMlInteropLastAffectedDriverBranch;

    /// <summary>
    /// Parses the NVIDIA driver branch (e.g. 617 for 617.14) from either the NVIDIA form ("617.14",
    /// as NVML reports it) or the Windows registry form ("32.0.16.1714", whose last five digits are 61714).
    /// </summary>
    internal static bool TryParseNvidiaDriverBranch(string? driverVersion, out int branch)
    {
        branch = 0;
        if (string.IsNullOrWhiteSpace(driverVersion))
        {
            return false;
        }

        string[] parts = driverVersion.Trim().Split('.');
        if (parts.Length == 4 &&
            parts[2].Length > 0 &&
            parts[3].Length is > 0 and <= 4 &&
            parts.All(static p => p.All(char.IsAsciiDigit)))
        {
            string digits = parts[2][^1] + parts[3].PadLeft(4, '0');
            return int.TryParse(digits[..3], NumberStyles.None, CultureInfo.InvariantCulture, out branch);
        }

        if (parts.Length >= 2 &&
            parts[0].Length == 3 &&
            int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out branch))
        {
            return true;
        }

        branch = 0;
        return false;
    }
}
