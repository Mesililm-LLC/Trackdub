using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.WinMlCatalog;

namespace Trackdub.Inference.Tests;

public sealed class OpenVinoCatalogReadinessProbeLinuxTests
{
    [Theory]
    [InlineData(LinuxPluginCatalogStatus.Ready, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.None, true)]
    [InlineData(LinuxPluginCatalogStatus.NotInstalled, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.EpNotPresent, false)]
    [InlineData(LinuxPluginCatalogStatus.Failed, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.EpRegisterFailed, false)]
    [InlineData(LinuxPluginCatalogStatus.NoDevices, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.HardwareNotSupported, false)]
    [InlineData(LinuxPluginCatalogStatus.Unsupported, WinMlCatalogPlatformRoute.None, WinMlCatalogReadinessBlocker.PlatformUnsupported, false)]
    public async Task Linux_catalog_status_maps_to_readiness_report(
        LinuxPluginCatalogStatus status,
        WinMlCatalogPlatformRoute route,
        WinMlCatalogReadinessBlocker blocker,
        bool ready)
    {
        if (OperatingSystem.IsWindows())
        {
            return; // The Windows TFM probes the Windows ML catalog instead.
        }

        var probe = new OpenVinoCatalogReadinessProbe(new StubCatalog(status));

        WinMlCatalogReadinessReport report = await probe.ProbeAsync(allowProviderDownloads: false);

        Assert.Equal(OpenVinoCatalogProviderIds.LinMl, report.ProviderId);
        Assert.Equal(route, report.Route);
        Assert.Equal(blocker, report.Blocker);
        Assert.Equal(ready, report.IsReady);
        Assert.Equal("stub detail", report.Detail);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Probe_passes_allowProviderDownloads_to_the_catalog(bool allowDownloads)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var catalog = new StubCatalog(LinuxPluginCatalogStatus.Ready);

        await new OpenVinoCatalogReadinessProbe(catalog).ProbeAsync(allowDownloads);

        Assert.Equal([allowDownloads], catalog.EnsureCalls);
    }

    [Theory]
    [InlineData(true, WinMlCatalogReadinessBlocker.EpDownloadFailed)]
    [InlineData(false, WinMlCatalogReadinessBlocker.EpRegisterFailed)]
    public async Task Failure_maps_to_download_or_registration_by_whether_downloads_were_allowed(
        bool allowDownloads,
        WinMlCatalogReadinessBlocker blocker)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        WinMlCatalogReadinessReport report = await new OpenVinoCatalogReadinessProbe(
            new StubCatalog(LinuxPluginCatalogStatus.Failed)).ProbeAsync(allowDownloads);

        Assert.Equal(blocker, report.Blocker);
    }

    [Fact]
    public async Task Non_Intel_hardware_is_not_eligible_and_the_catalog_is_not_touched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var catalog = new StubCatalog(LinuxPluginCatalogStatus.NotInstalled);

        WinMlCatalogReadinessReport report = await new OpenVinoCatalogReadinessProbe(catalog, () => false)
            .ProbeAsync(allowProviderDownloads: true);

        Assert.Equal(WinMlCatalogPlatformRoute.LinMlCatalog, report.Route);
        Assert.Equal(WinMlCatalogReadinessBlocker.HardwareNotSupported, report.Blocker);
        Assert.False(report.IsHardwareEligible);
        Assert.Empty(catalog.EnsureCalls);
    }

    [Theory]
    [InlineData("vendor_id\t: GenuineIntel\n", true)]
    [InlineData("vendor_id\t: AuthenticAMD\n", false)]
    [InlineData(null, false)]
    public void Intel_cpu_is_detected_from_cpuinfo(string? cpuInfo, bool expected) =>
        Assert.Equal(expected, LinuxIntelHardwareGate.HasIntelCpu(cpuInfo));

    [Theory]
    [InlineData("0x8086", "0x030000", true)]
    [InlineData("0x8086", "0x120000", true)]
    [InlineData("0x8086", "0x060400", false)]
    [InlineData("0x10de", "0x030000", false)]
    public void Intel_display_or_accelerator_pci_device_is_detected(string vendor, string pciClass, bool expected)
    {
        string root = Path.Join(Path.GetTempPath(), $"trackdub-pci-{Guid.NewGuid():N}");
        // Real sysfs names contain colons, which Windows paths reject; the gate does not read the name.
        string device = Path.Join(root, "0000-00-02.0");
        Directory.CreateDirectory(device);
        try
        {
            File.WriteAllText(Path.Join(device, "vendor"), vendor + "\n");
            File.WriteAllText(Path.Join(device, "class"), pciClass + "\n");

            Assert.Equal(expected, LinuxIntelHardwareGate.HasIntelPciDevice(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubCatalog(LinuxPluginCatalogStatus status) : ILinuxOpenVinoCatalog
    {
        public List<bool> EnsureCalls { get; } = [];

        public Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken)
        {
            EnsureCalls.Add(allowDownloads);
            return Task.FromResult(new LinuxPluginCatalogResult(status, "stub detail"));
        }

        public bool TryAppend(Microsoft.ML.OnnxRuntime.SessionOptions options, bool cpuOnly, out string? failureReason)
        {
            failureReason = null;
            return false;
        }
    }
}
