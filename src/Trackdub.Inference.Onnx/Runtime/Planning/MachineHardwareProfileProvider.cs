using System.Diagnostics;
using System.Runtime.InteropServices;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Planning;

public sealed class MachineHardwareProfileProvider : IHardwareProfileProvider
{
    public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string operatingSystem = OperatingSystem.IsWindows()
            ? "windows"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS()
                    ? "macos"
                    : "unknown";

        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        string? gpuDescription = null;
        string? gpuDriverVersion = null;
        string? cpuName = null;
        bool hasGpu = false;
        long dedicatedVramMb = 0;
        long totalRamMb = 0;

        try
        {
            totalRamMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));
        }
        catch (Exception ex) { Debug.WriteLine($"[MachineHardwareProfileProvider] Failed to read GC memory info for RAM estimation: {ex.Message}"); }

        if (OperatingSystem.IsWindows())
        {
            hasGpu = true;
            gpuDescription = "Windows GPU route available for DirectML probing.";

            try
            {
#pragma warning disable CA1416
                using var cpuKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim();

                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                if (key != null)
                {
                    foreach (string subkeyName in key.GetSubKeyNames())
                    {
                        using var subkey = key.OpenSubKey(subkeyName);
                        var desc = subkey?.GetValue("DriverDesc") as string;
                        if (!string.IsNullOrEmpty(desc))
                        {
                            gpuDescription = desc;

                            object? vramObj = subkey?.GetValue("HardwareInformation.MemorySize")
                                          ?? subkey?.GetValue("HardwareInformation.qwMemorySize");
                            dedicatedVramMb = vramObj switch
                            {
                                long vramLong => vramLong / (1024 * 1024),
                                int vramInt => (long)vramInt / (1024 * 1024),
                                byte[] { Length: 8 } b8 => (long)(BitConverter.ToUInt64(b8, 0) / (1024 * 1024)),
                                byte[] { Length: 4 } b4 => (long)(BitConverter.ToUInt32(b4, 0) / (1024 * 1024)),
                                _ => dedicatedVramMb
                            };

                            // Keep in sync with gpuDescription: both must come from the same
                            // selected adapter, so a later adapter overwrites both together.
                            gpuDriverVersion = subkey?.GetValue("DriverVersion") as string;

                            if (desc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                                break;
                        }
                    }
                }
#pragma warning restore CA1416
            }
            catch (Exception ex) { Debug.WriteLine($"[MachineHardwareProfileProvider] Failed to read GPU/VRAM from Windows registry: {ex.Message}"); }
        }
        else if (OperatingSystem.IsMacOS())
        {
            hasGpu = true;
            bool isAppleSilicon =
                RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                && OperatingSystem.IsMacOS();
            gpuDescription = isAppleSilicon ? "Apple Silicon (ANE + Metal)" : "Mac GPU (Metal)";

            try
            {
                using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "sysctl",
                    Arguments = "-n machdep.cpu.brand_string",
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                });
                cpuName = proc?.StandardOutput.ReadLine()?.Trim();
            }
            catch (Exception ex) { Debug.WriteLine($"[MachineHardwareProfileProvider] Failed to read CPU info via macOS sysctl: {ex.Message}"); }

            // Apple Silicon: unified memory — VRAM = total RAM
            if (isAppleSilicon && totalRamMb > 0)
                dedicatedVramMb = totalRamMb;
        }
        else if (OperatingSystem.IsLinux())
        {
            var sysfs = new PhysicalSysfsReader();
            hasGpu = LinuxPciDeviceScanner.HasAnyGpu(sysfs);
            gpuDescription = hasGpu ? LinuxPciDeviceScanner.DetectPrimaryVendorName(sysfs) : null;

            // Sysfs yields only a generic vendor name ("NVIDIA") and no driver
            // version, and inside containers it may not be visible at all.
            // NVML (injected by the NVIDIA container toolkit with --gpus all)
            // gives the real device name (for architecture classification) and
            // driver version (for the engine-cache fingerprint).
            (string? nvmlDeviceName, string? nvmlDriverVersion) = AcceleratorVramProbe.QueryNvmlGpuIdentity();
            if (!string.IsNullOrWhiteSpace(nvmlDeviceName))
            {
                gpuDescription = nvmlDeviceName;
                hasGpu = true;
            }

            if (!string.IsNullOrWhiteSpace(nvmlDriverVersion))
                gpuDriverVersion = nvmlDriverVersion;
            try
            {
                string? cpuLine = System.IO.File.ReadAllLines("/proc/cpuinfo")
                    .FirstOrDefault(l => l.StartsWith("model name", StringComparison.OrdinalIgnoreCase));
                if (cpuLine is not null)
                    cpuName = cpuLine.Split(':').LastOrDefault()?.Trim();
            }
            catch (Exception ex) { Debug.WriteLine($"[MachineHardwareProfileProvider] Failed to read CPU info from /proc/cpuinfo: {ex.Message}"); }
        }

        return Task.FromResult(new HardwareProfile(
            operatingSystem,
            architecture,
            hasGpu,
            GpuDescription: gpuDescription,
            CpuName: cpuName,
            TotalRamMb: totalRamMb,
            DedicatedVramMb: dedicatedVramMb,
            NvidiaGpuArchitecture: ResolveNvidiaGpuArchitecture(gpuDescription),
            GpuDriverVersion: gpuDriverVersion,
            OrtRuntimeVersion: EpContextArtifact.HostOrtRuntimeVersion));
    }

    private static NvidiaGpuArchitectureBucket ResolveNvidiaGpuArchitecture(string? gpuDescription)
    {
        string? overrideName = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME");
        if (!string.IsNullOrWhiteSpace(overrideName))
        {
            return NvidiaGpuArchitectureClassifier.ClassifyFromName(overrideName);
        }

        return NvidiaGpuArchitectureClassifier.ClassifyFromGpuDescription(gpuDescription);
    }
}
