namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>
/// Linux counterpart to the Windows Intel catalog hardware gate: the OpenVINO plugin is offered
/// only with an Intel CPU, or an Intel display or processing-accelerator (NPU) PCI device.
/// </summary>
internal static class LinuxIntelHardwareGate
{
    private const string IntelPciVendor = "0x8086";

    public static bool IsEligible() =>
        HasIntelCpu(SafeReadAllText("/proc/cpuinfo")) || HasIntelPciDevice("/sys/bus/pci/devices");

    internal static bool HasIntelCpu(string? cpuInfo) =>
        cpuInfo is not null && cpuInfo.Contains("GenuineIntel", StringComparison.Ordinal);

    internal static bool HasIntelPciDevice(string pciDevicesRoot)
    {
        if (!Directory.Exists(pciDevicesRoot))
        {
            return false;
        }

        // Enumeration is lazy, so an unreadable sysfs can also fail mid-loop.
        try
        {
            foreach (string device in Directory.EnumerateDirectories(pciDevicesRoot))
            {
                string? vendor = SafeReadAllText(Path.Join(device, "vendor"))?.Trim();
                string? pciClass = SafeReadAllText(Path.Join(device, "class"))?.Trim();
                if (string.Equals(vendor, IntelPciVendor, StringComparison.OrdinalIgnoreCase)
                    && pciClass is not null
                    && (pciClass.StartsWith("0x03", StringComparison.OrdinalIgnoreCase)
                        || pciClass.StartsWith("0x12", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static string? SafeReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
