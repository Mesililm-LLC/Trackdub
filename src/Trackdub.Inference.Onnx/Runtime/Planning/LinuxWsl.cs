namespace Trackdub.Inference.Onnx.Runtime.Planning;

/// <summary>WSL detection by file probes only, so it is safe to call on any OS.</summary>
internal static class LinuxWsl
{
    // WSL2 has no /proc/driver/nvidia: the Windows driver reaches Linux through /dev/dxg, and the
    // CUDA driver library is projected into /usr/lib/wsl/lib.
    private const string GpuPassthroughDevice = "/dev/dxg";
    private const string WslCudaDriverLibrary = "/usr/lib/wsl/lib/libcuda.so.1";

    public static bool IsWsl() =>
        IsWsl(File.Exists, static path => File.ReadAllText(path));

    internal static bool IsWsl(Func<string, bool> fileExists, Func<string, string> readAllText)
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return fileExists("/proc/sys/fs/binfmt_misc/WSLInterop")
                || (fileExists("/proc/version")
                    && readAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool HasNvidiaGpuPassthrough() => HasNvidiaGpuPassthrough(File.Exists);

    internal static bool HasNvidiaGpuPassthrough(Func<string, bool> fileExists) =>
        OperatingSystem.IsLinux() && fileExists(GpuPassthroughDevice) && fileExists(WslCudaDriverLibrary);
}
