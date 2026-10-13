namespace Trackdub.Inference.Onnx.Runtime.Planning;

/// <summary>WSL detection by file probes only, so it is safe to call on any OS.</summary>
internal static class LinuxWsl
{
    // WSL2 exposes /dev/dxg and /usr/lib/wsl/lib/libcuda.so.1. Docker's NVIDIA runtime may instead
    // expose the same driver library under /usr/lib/x86_64-linux-gnu. Accept both layouts.
    private const string GpuPassthroughDevice = "/dev/dxg";
    private static readonly string[] NvidiaCudaDriverLibraries =
    [
        "/usr/lib/wsl/lib/libcuda.so.1",
        "/usr/lib/x86_64-linux-gnu/libcuda.so.1",
        "/lib/x86_64-linux-gnu/libcuda.so.1"
    ];

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
            // Detection is best-effort: an unreadable /proc entry means "not WSL", and the caller
            // then takes the ordinary Linux path, which reports its own failure if the GPU is unusable.
            return false;
        }
    }

    public static bool HasNvidiaGpuPassthrough() => HasNvidiaGpuPassthrough(File.Exists);

    internal static bool HasNvidiaGpuPassthrough(Func<string, bool> fileExists) =>
        OperatingSystem.IsLinux()
        && (fileExists(GpuPassthroughDevice) || NvidiaCudaDriverLibraries.Any(fileExists))
        && NvidiaCudaDriverLibraries.Any(fileExists);
}
