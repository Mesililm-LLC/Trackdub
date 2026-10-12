using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Trackdub.Inference.Onnx.Runtime;

/// <summary>
/// Opt-in process-level EcoQoS (Windows power throttling) for inference host processes.
/// </summary>
/// <remarks>
/// <para>
/// MS "Efficiency tips for Windows ML inference" (CPU workloads): opt background inference
/// that does not contribute to the foreground experience into the power-throttling
/// "execution speed" policy via <c>SetProcessInformation(ProcessPowerThrottling)</c> —
/// an intent signal the OS scheduler maps to efficiency-oriented scheduling, not a hard cap.
/// </para>
/// <para>
/// Read-back uses <c>GetProcessInformation</c>; its <c>Version</c> field is IN/OUT and MUST be
/// pre-set to <c>PROCESS_POWER_THROTTLING_CURRENT_VERSION (1)</c> or the call fails with
/// <c>ERROR_INVALID_PARAMETER (87)</c> (verified on Windows 11 build 26200). Every result is
/// reported honestly: no "enabled" claim without a verified read-back.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsEcoQos
{
    /// <summary>Set to <c>1</c> on the host process (inherited by child workers) to opt in.</summary>
    public const string EnvironmentVariable = "TRACKDUB_ECOQOS";

    private const uint CurrentVersion = 1;
    private const uint ExecutionSpeedFlag = 0x1; // PROCESS_POWER_THROTTLING_EXECUTION_SPEED
    private const int ProcessPowerThrottling = 4; // PROCESS_INFORMATION_CLASS

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessPowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool SetProcessInformation(
        IntPtr hProcess, int processInformationClass, ref ProcessPowerThrottlingState info, uint size);

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool GetProcessInformation(
        IntPtr hProcess, int processInformationClass, ref ProcessPowerThrottlingState info, uint size);

    /// <summary>Verified state: execution-speed throttling is explicitly requested (state mask set).</summary>
    public static bool IsExecutionSpeedThrottled
    {
        get
        {
            var state = new ProcessPowerThrottlingState { Version = CurrentVersion };
            if (!GetProcessInformation(
                    (IntPtr)(-1), ProcessPowerThrottling, ref state,
                    (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
            {
                return false;
            }
            return (state.ControlMask & ExecutionSpeedFlag) != 0 &&
                   (state.StateMask & ExecutionSpeedFlag) != 0;
        }
    }

    /// <summary>Enables EcoQoS for the current process. Returns false with a reason on failure.</summary>
    public static (bool Applied, string? Error) EnableExecutionSpeedThrottling() =>
        SetExecutionSpeedThrottle(enabled: true);

    /// <summary>
    /// Returns the process to OS-managed scheduling (neutral: control mask cleared). Callers must
    /// invoke this to undo an enable; an EcoQoS enable is never left behind on a shared host.
    /// </summary>
    public static (bool Applied, string? Error) DisableExecutionSpeedThrottling() =>
        SetExecutionSpeedThrottle(enabled: false);

    private static (bool Applied, string? Error) SetExecutionSpeedThrottle(bool enabled)
    {
        var state = new ProcessPowerThrottlingState
        {
            Version = CurrentVersion,
            // Enable: control the execution-speed policy and set its state to throttled.
            // Disable: clear the control mask so the OS manages scheduling again (verified
            // read-back returns 0/0 after this form).
            ControlMask = enabled ? ExecutionSpeedFlag : 0,
            StateMask = enabled ? ExecutionSpeedFlag : 0,
        };
        if (!SetProcessInformation(
                (IntPtr)(-1), ProcessPowerThrottling, ref state,
                (uint)Marshal.SizeOf<ProcessPowerThrottlingState>()))
        {
            return (false, $"SetProcessInformation failed with GetLastError={Marshal.GetLastWin32Error()}.");
        }
        return (true, null);
    }
}
