using System.Runtime.InteropServices;
using System.Text;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// DI-free GPU VRAM probes for static pool defaults. Self-contained (raw DXGI
/// P/Invoke on Windows, plain /sys + /proc file reads on Linux) so the portable
/// TFM, which excludes the full device enumerators, can still scale the
/// accelerator admission budget. Every probe returns 0 on unknown/failure and
/// the caller treats 0 as unknown (floor budget).
/// </summary>
internal static class AcceleratorVramProbe
{
#if WINDOWS
    /// <summary>
    /// Largest dedicated video memory (MB) across hardware DXGI adapters.
    /// Windows leg only.
    /// </summary>
    internal static long QueryMaxDedicatedVramMb()
    {
        try
        {
            int hr = NativeMethods.CreateDXGIFactory1(ref NativeMethods.IID_IDXGIFactory1, out nint factoryPtr);
            if (hr < 0 || factoryPtr == 0)
                return 0;

            try
            {
                ulong maxBytes = 0;
                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    hr = NativeMethods.IDXGIFactory1_EnumAdapters1(factoryPtr, adapterIndex, out nint adapterPtr);
                    if (hr < 0)
                        break;

                    try
                    {
                        var desc = new DxgiAdapterDesc1();
                        hr = NativeMethods.IDXGIAdapter1_GetDesc1(adapterPtr, ref desc);
                        if (hr < 0)
                            continue;

                        if ((desc.Flags & NativeMethods.DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                            continue;

                        if (desc.DedicatedVideoMemory > maxBytes)
                            maxBytes = desc.DedicatedVideoMemory;
                    }
                    catch
                    {
                        continue;
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                }

                return (long)(maxBytes / (1024 * 1024));
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        catch
        {
            return 0;
        }
    }
#endif

    /// <summary>
    /// Largest dedicated video memory (MB) across PCI GPU devices on Linux:
    /// NVIDIA via /proc/driver/nvidia, AMD via amdgpu sysfs, Intel iGPU reports 0
    /// (shared memory — floor budget applies). Pure managed file IO so it compiles
    /// on every leg; returns 0 off-Linux or on any failure.
    /// </summary>
    internal static long QueryLinuxMaxDedicatedVramMb()
    {
        long sysfsMb = QueryLinuxSysfsMaxDedicatedVramMb();
        if (sysfsMb > 0)
            return sysfsMb;

        // Container runtimes often hide /proc/driver/nvidia even with --gpus all,
        // which silently dropped the budget to the floor. NVML is injected by the
        // NVIDIA container toolkit whenever a GPU is passed through, so it is the
        // reliable in-container source.
        return QueryNvmlMaxDedicatedVramMb();
    }

    private static long QueryLinuxSysfsMaxDedicatedVramMb()
    {
        if (!OperatingSystem.IsLinux())
            return 0;

        try
        {
            const string pciBase = "/sys/bus/pci/devices";
            if (!Directory.Exists(pciBase))
                return 0;

            long maxMb = 0;
            foreach (string deviceDir in Directory.EnumerateDirectories(pciBase))
            {
                try
                {
                    string? classText = ReadTextFile(Path.Join(deviceDir, "class"))?.Trim();
                    if (classText is null || !TryParseHex(classText, out uint classCode))
                        continue;

                    uint classGroup = classCode & 0xFFFF00;
                    if (classGroup != PciClassVga && classGroup != PciClass3DCtrl && classGroup != PciClassDisplay)
                        continue;

                    string? vendorText = ReadTextFile(Path.Join(deviceDir, "vendor"))?.Trim();
                    if (vendorText is null || !TryParseHex(vendorText, out uint vendorId))
                        continue;

                    long vramMb = vendorId switch
                    {
                        VendorNvidia => ReadNvidiaVramMb(Path.GetFileName(deviceDir)),
                        VendorAmd => ReadAmdVramMb(deviceDir),
                        _ => 0,
                    };
                    if (vramMb > maxMb)
                        maxMb = vramMb;
                }
                catch
                {
                    continue;
                }
            }

            return maxMb;
        }
        catch
        {
            return 0;
        }
    }

    private const uint PciClassVga = 0x030000;
    private const uint PciClass3DCtrl = 0x030200;
    private const uint PciClassDisplay = 0x038000;

    private const uint VendorNvidia = 0x10de;
    private const uint VendorAmd = 0x1002;

    private static long ReadNvidiaVramMb(string address)
    {
        // /proc format: "Video Memory:      8192 MB"
        string? text = ReadTextFile($"/proc/driver/nvidia/gpus/{address}/information");
        return text is null ? 0 : ParseNvidiaInformationMb(text);
    }

    internal static long ParseNvidiaInformationMb(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("Video Memory:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Formats observed: "8192 MB", "8192MB", "8192 MiB".
            string token = line["Video Memory:".Length..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            token = token.TrimEnd("mMbBiI".ToCharArray());
            if (long.TryParse(token, out long mb) && mb > 0)
                return mb;
        }

        return 0;
    }

    internal static long MbFromNvmlMemoryInfo(ulong totalBytes) =>
        (long)(totalBytes / 1024 / 1024);

    /// <summary>
    /// Decodes an NVML fixed-size string buffer: NUL-padded UTF-8, terminated
    /// at the first NUL. Returns null for null/empty/whitespace-only buffers
    /// so callers treat the value as unknown.
    /// </summary>
    internal static string? DecodeNvmlString(byte[]? buffer)
    {
        if (buffer is null || buffer.Length == 0)
            return null;

        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;
        if (length == 0)
            return null;

        string decoded = Encoding.UTF8.GetString(buffer, 0, length).Trim();
        return decoded.Length == 0 ? null : decoded;
    }

    private static bool TryLoadNvmlLibrary(out nint library) =>
        NativeLibrary.TryLoad("libnvidia-ml.so.1", out library) ||
        NativeLibrary.TryLoad("libnvidia-ml.so", out library);

    /// <summary>
    /// Device name and driver version of the first NVIDIA device via NVML
    /// (libnvidia-ml.so.1, injected by the NVIDIA container toolkit with
    /// --gpus all), plus the system driver version. Linux only; returns
    /// (null, null) on any failure (library absent, NVML error, empty
    /// strings) so callers treat identity as unknown. Sysfs/proc do not
    /// expose a usable GPU name or driver version inside containers, which
    /// left the hardware fingerprint at Unknown/unknown there.
    /// </summary>
    internal static (string? DeviceName, string? DriverVersion) QueryNvmlGpuIdentity()
    {
        if (!OperatingSystem.IsLinux())
            return (null, null);

        nint library = nint.Zero;
        try
        {
            if (!TryLoadNvmlLibrary(out library))
                return (null, null);

            if (!TryGetExport<NvmlInitDelegate>(library, "nvmlInit_v2", out var init) ||
                !TryGetExport<NvmlDeviceGetHandleDelegate>(library, "nvmlDeviceGetHandleByIndex_v2", out var getHandle) ||
                !TryGetExport<NvmlSystemGetDriverVersionDelegate>(library, "nvmlSystemGetDriverVersion", out var getDriverVersion) ||
                !TryGetExport<NvmlDeviceGetNameDelegate>(library, "nvmlDeviceGetName", out var getName))
            {
                return (null, null);
            }

            if (init() != 0)
                return (null, null);

            try
            {
                string? driverVersion = null;
                byte[] driverBuffer = new byte[80];
                if (getDriverVersion(driverBuffer, (uint)driverBuffer.Length) == 0)
                    driverVersion = DecodeNvmlString(driverBuffer);

                string? deviceName = null;
                if (getHandle(0, out nint device) == 0 && device != nint.Zero)
                {
                    byte[] nameBuffer = new byte[128];
                    if (getName(device, nameBuffer, (uint)nameBuffer.Length) == 0)
                        deviceName = DecodeNvmlString(nameBuffer);
                }

                return (deviceName, driverVersion);
            }
            finally
            {
                if (TryGetExport<NvmlShutdownDelegate>(library, "nvmlShutdown", out var shutdown))
                    _ = shutdown();
            }
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (library != nint.Zero)
                NativeLibrary.Free(library);
        }
    }

    /// <summary>
    /// Largest dedicated VRAM (MB) across NVIDIA devices via NVML
    /// (libnvidia-ml.so.1, injected by the NVIDIA container toolkit with
    /// --gpus all). Linux only; returns 0 on any failure (library absent,
    /// no devices, NVML error) so callers treat it as unknown.
    /// </summary>
    internal static long QueryNvmlMaxDedicatedVramMb()
    {
        if (!OperatingSystem.IsLinux())
            return 0;

        nint library = nint.Zero;
        try
        {
            if (!TryLoadNvmlLibrary(out library))
            {
                return 0;
            }

            if (!TryGetExport<NvmlInitDelegate>(library, "nvmlInit_v2", out var init) ||
                !TryGetExport<NvmlDeviceGetCountDelegate>(library, "nvmlDeviceGetCount_v2", out var getCount) ||
                !TryGetExport<NvmlDeviceGetHandleDelegate>(library, "nvmlDeviceGetHandleByIndex_v2", out var getHandle) ||
                !TryGetExport<NvmlDeviceGetMemoryInfoDelegate>(library, "nvmlDeviceGetMemoryInfo", out var getMemoryInfo))
            {
                return 0;
            }

            if (init() != 0)
                return 0;

            try
            {
                if (getCount(out uint count) != 0 || count == 0)
                    return 0;

                long maxMb = 0;
                for (uint i = 0; i < count; i++)
                {
                    if (getHandle(i, out nint device) != 0)
                        continue;

                    if (getMemoryInfo(device, out NvmlMemory memory) != 0)
                        continue;

                    long mb = MbFromNvmlMemoryInfo(memory.Total);
                    if (mb > maxMb)
                        maxMb = mb;
                }

                return maxMb;
            }
            finally
            {
                if (TryGetExport<NvmlShutdownDelegate>(library, "nvmlShutdown", out var shutdown))
                    _ = shutdown();
            }
        }
        catch
        {
            return 0;
        }
        finally
        {
            if (library != nint.Zero)
                NativeLibrary.Free(library);
        }
    }

    private static bool TryGetExport<TDelegate>(nint library, string name, out TDelegate fn)
        where TDelegate : Delegate
    {
        fn = null!;
        if (!NativeLibrary.TryGetExport(library, name, out nint address) || address == nint.Zero)
            return false;

        fn = Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
        return true;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlInitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetCountDelegate(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetHandleDelegate(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetMemoryInfoDelegate(nint device, out NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlSystemGetDriverVersionDelegate(byte[] buffer, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvmlDeviceGetNameDelegate(nint device, byte[] buffer, uint length);

    /// <summary>nvmlMemory_t: total/free/used bytes, in that order.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    private static long ReadAmdVramMb(string deviceDir)
    {
        string? bytesText = null;
        try
        {
            string? drmLink = Directory.EnumerateDirectories(deviceDir)
                .FirstOrDefault(d => Path.GetFileName(d).StartsWith("drm", StringComparison.Ordinal));
            if (drmLink is null)
                return 0;

            bytesText = ReadTextFile(Path.Join(deviceDir, "mem_info_vram_total"));
        }
        catch
        {
            return 0;
        }

        return ParseAmdVramBytes(bytesText);
    }

    internal static long ParseAmdVramBytes(string? bytesText) =>
        bytesText is not null && long.TryParse(bytesText.Trim(), out long bytes) && bytes > 0
            ? bytes / 1024 / 1024
            : 0;

    internal static bool TryParseHex(string text, out uint value)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart("0x").TrimStart("0X");
        return uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static string? ReadTextFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

#if WINDOWS
    private static class NativeMethods
    {
        public static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll", ExactSpelling = true, PreserveSig = true)]
        public static extern int CreateDXGIFactory1(ref Guid riid, out nint ppFactory);

        public static int IDXGIFactory1_EnumAdapters1(nint factory, uint adapterIndex, out nint adapter)
        {
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12.
            nint vtable = Marshal.ReadIntPtr(factory);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 12 * nint.Size);

            var fn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(fnPtr);
            return fn(factory, adapterIndex, out adapter);
        }

        public static int IDXGIAdapter1_GetDesc1(nint adapter, ref DxgiAdapterDesc1 desc)
        {
            // IDXGIAdapter1::GetDesc1 is vtable slot 10.
            nint vtable = Marshal.ReadIntPtr(adapter);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 10 * nint.Size);

            var fn = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(fnPtr);
            return fn(adapter, ref desc);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EnumAdapters1Delegate(nint thisPtr, uint adapterIndex, out nint adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetDesc1Delegate(nint thisPtr, ref DxgiAdapterDesc1 desc);
    }

    /// <summary>
    /// Mirrors the native DXGI_ADAPTER_DESC1 prefix through DedicatedVideoMemory;
    /// field order must match the native layout.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }
#endif
}
