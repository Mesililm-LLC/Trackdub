using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime;

[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceEnumerator : IDeviceEnumerator
{
    private readonly IOpenVinoAvailabilityProvider _openVino;
    private readonly ISysfsReader _sysfs;
    private readonly ILogger<LinuxDeviceEnumerator> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IReadOnlyList<DeviceEntry>? _cachedDevices;

    public LinuxDeviceEnumerator(
        IOpenVinoAvailabilityProvider openVino,
        ISysfsReader sysfs,
        ILogger<LinuxDeviceEnumerator> logger)
    {
        _openVino = openVino ?? throw new ArgumentNullException(nameof(openVino));
        _sysfs = sysfs ?? throw new ArgumentNullException(nameof(sysfs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<DeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cached = _cachedDevices;
        if (cached is not null)
            return cached;

        return await EnumerateAndCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeviceEntry>> ReEnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await EnumerateAndCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DeviceEntry>> EnumerateAndCacheAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = EnumerateDevices();
            _cachedDevices = devices;
            return devices;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private IReadOnlyList<DeviceEntry> EnumerateDevices()
    {
        var entries = new List<DeviceEntry>();
        int index = 0;

        IReadOnlyList<LinuxPciDeviceScanner.PciGpuDevice> gpus =
            LinuxPciDeviceScanner.EnumerateGpus(_sysfs);

        foreach (LinuxPciDeviceScanner.PciGpuDevice gpu in gpus)
        {
            (DeviceKind kind, string vendor, IReadOnlyList<ExecutionProviderKind> providers) =
                gpu.Vendor switch
                {
                    LinuxPciDeviceScanner.GpuVendor.Nvidia =>
                        (DeviceKind.DiscreteGpu, "NVIDIA",
                         (IReadOnlyList<ExecutionProviderKind>)[ExecutionProviderKind.Cuda, ExecutionProviderKind.TensorRt]),

                    LinuxPciDeviceScanner.GpuVendor.Amd =>
                        (DeviceKind.DiscreteGpu, "AMD",
                         BuildAmdSupportedProviders()),

                    LinuxPciDeviceScanner.GpuVendor.Intel =>
                        (DeviceKind.IntegratedGpu, "Intel",
                         (IReadOnlyList<ExecutionProviderKind>)[ExecutionProviderKind.OpenVino]),

                    _ =>
                        (DeviceKind.DiscreteGpu, "Unknown",
                         (IReadOnlyList<ExecutionProviderKind>)[ExecutionProviderKind.Cpu]),
                };

            entries.Add(new DeviceEntry(
                Kind: kind,
                DeviceIndex: index++,
                AdapterDescription: $"{vendor} GPU ({gpu.Address})",
                VendorName: vendor,
                DedicatedVramMb: (int)Math.Min(gpu.VramMb, int.MaxValue),
                SharedMemoryMb: 0,
                SupportedProviders: providers));

            _logger.LogDebug(
                "Linux PCI GPU: {Address} vendor={Vendor} vram={VramMb}MB",
                gpu.Address, gpu.Vendor, gpu.VramMb);
        }

        if (LinuxPciDeviceScanner.HasIntelNpu(_sysfs))
        {
            entries.Add(new DeviceEntry(
                Kind: DeviceKind.Npu,
                DeviceIndex: index++,
                AdapterDescription: "Intel NPU (VPU)",
                VendorName: "Intel",
                DedicatedVramMb: 0,
                SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.OpenVino]));

            _logger.LogDebug("Intel NPU detected via intel_vpu driver.");
        }

        // CPU always present
        var cpuProviderList = new List<ExecutionProviderKind> { ExecutionProviderKind.Cpu };
        if (_openVino.UseOpenVinoCpuProxy)
        {
            cpuProviderList.Add(ExecutionProviderKind.OpenVino);
        }
        if (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
            && Dnnl.DnnlOrtProbe.IsProviderListed())
        {
            cpuProviderList.Add(ExecutionProviderKind.Dnnl);
        }
        IReadOnlyList<ExecutionProviderKind> cpuProviders = cpuProviderList;

        entries.Add(new DeviceEntry(
            Kind: DeviceKind.Cpu,
            DeviceIndex: index,
            AdapterDescription: "CPU",
            VendorName: "Generic",
            DedicatedVramMb: 0,
            SharedMemoryMb: 0,
            SupportedProviders: cpuProviders));

        return entries;
    }

    private static IReadOnlyList<ExecutionProviderKind> BuildAmdSupportedProviders()
    {
        var providers = new List<ExecutionProviderKind> { ExecutionProviderKind.Cpu };
        // With the ROCm MIGraphX runtime present, the LinML catalog's plugin EP can serve MIGraphX
        // even before it is installed; bootstrap installs or falls back to CPU.
        if (MigraphxOrtProbe.IsProviderListed() || LinuxMigraphxRuntimeProbe.IsRuntimePresent())
        {
            providers.Insert(0, ExecutionProviderKind.Migraphx);
        }

        return providers;
    }
}
