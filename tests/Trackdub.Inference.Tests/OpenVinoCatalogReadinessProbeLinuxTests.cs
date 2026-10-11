using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.WinMlCatalog;

namespace Trackdub.Inference.Tests;

public sealed class OpenVinoCatalogReadinessProbeLinuxTests
{
    [Theory]
    [InlineData(LinuxOpenVinoCatalogStatus.Ready, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.None, true)]
    [InlineData(LinuxOpenVinoCatalogStatus.NotInstalled, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.EpNotPresent, false)]
    [InlineData(LinuxOpenVinoCatalogStatus.Failed, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.EpDownloadFailed, false)]
    [InlineData(LinuxOpenVinoCatalogStatus.NoDevices, WinMlCatalogPlatformRoute.LinMlCatalog, WinMlCatalogReadinessBlocker.HardwareNotSupported, false)]
    [InlineData(LinuxOpenVinoCatalogStatus.Unsupported, WinMlCatalogPlatformRoute.None, WinMlCatalogReadinessBlocker.PlatformUnsupported, false)]
    public async Task Linux_catalog_status_maps_to_readiness_report(
        LinuxOpenVinoCatalogStatus status,
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

        var catalog = new StubCatalog(LinuxOpenVinoCatalogStatus.Ready);

        await new OpenVinoCatalogReadinessProbe(catalog).ProbeAsync(allowDownloads);

        Assert.Equal([allowDownloads], catalog.EnsureCalls);
    }

    private sealed class StubCatalog(LinuxOpenVinoCatalogStatus status) : ILinuxOpenVinoCatalog
    {
        public List<bool> EnsureCalls { get; } = [];

        public Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken)
        {
            EnsureCalls.Add(allowDownloads);
            return Task.FromResult(new LinuxOpenVinoCatalogResult(status, "stub detail"));
        }

        public bool TryAppend(Microsoft.ML.OnnxRuntime.SessionOptions options, bool cpuOnly, out string? failureReason)
        {
            failureReason = null;
            return false;
        }
    }
}
