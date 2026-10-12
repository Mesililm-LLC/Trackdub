using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Runtime.Migraphx;

public static class MigraphxProviderConstants
{
    public const string OrtExecutionProviderName = "MIGraphXExecutionProvider";

    public static string ProviderIdWinMl => MigraphxProviderIds.WinMl;

    public static string ProviderIdRocm => MigraphxProviderIds.Rocm;

    public static string ProviderIdLinMl => MigraphxProviderIds.LinMl;

    /// <summary>Windows 11 24H2 minimum build per Windows ML MIGraphX documentation.</summary>
    public const int WindowsMinimumBuild = 26100;

    /// <summary>AMD GPU driver version required for Windows ML MIGraphX EP (exact match policy).</summary>
    public const string WindowsRequiredAmdDriverVersion = "25.10.13.09";

    /// <summary>The ROCm MIGraphX C API library the Linux plugin EP links against.</summary>
    public const string LinuxRuntimeLibrary = "libmigraphx_c.so.3";

    public const string LinuxInstallHint =
        "Install ROCm with MIGraphX 2.17 or newer (libmigraphx_c.so.3 under /opt/rocm/lib); Trackdub then " +
        "installs AMD's MIGraphX plugin EP through the LinML catalog.";
}
