using Trackdub.Composition.Runtime;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class LinuxOpenVinoInstallPathTests
{
    [Theory]
    [InlineData(WinMlCatalogReadinessBlocker.EpNotPresent)]
    [InlineData(WinMlCatalogReadinessBlocker.EpDownloadFailed)]
    public async Task Readiness_offers_install_on_the_LinML_route(WinMlCatalogReadinessBlocker blocker)
    {
        var service = new OpenVinoCatalogRuntimeReadinessService(new StubProbe(LinuxReport(blocker, ready: false)));

        WinMlCatalogRuntimeReadinessSnapshot snapshot = await service.ProbeAsync(allowProviderDownloads: false);

        Assert.True(snapshot.IsSupportedPlatform);
        Assert.True(snapshot.IsHardwareEligible);
        Assert.True(snapshot.CanInstallWinMlProvider);
        Assert.Equal("Linux (LinML catalog)", snapshot.RouteDisplay);
        Assert.Contains("LinML", snapshot.InstallHint, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Readiness_reports_ready_on_the_LinML_route()
    {
        var service = new OpenVinoCatalogRuntimeReadinessService(
            new StubProbe(LinuxReport(WinMlCatalogReadinessBlocker.None, ready: true)));

        WinMlCatalogRuntimeReadinessSnapshot snapshot = await service.ProbeAsync(allowProviderDownloads: false);

        Assert.True(snapshot.IsReady);
        Assert.False(snapshot.CanInstallWinMlProvider);
        Assert.Equal("Linux (LinML catalog)", snapshot.RouteDisplay);
    }

#if !WINDOWS
    [Fact]
    public async Task Installer_prepares_the_plugin_with_downloads_allowed_on_Linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var probe = new StubProbe(LinuxReport(WinMlCatalogReadinessBlocker.None, ready: true));
        var installer = new OpenVinoCatalogEpInstaller(probe);

        var result = await installer.EnsureInstalledAsync(new Progress<string>());

        Assert.True(result.Succeeded);
        Assert.Equal([true], probe.AllowDownloadsCalls);
    }

    // Opt-in: downloads the pinned plugin into LinML's install root on an x64 Linux host.
    [Fact]
    public async Task Installer_end_to_end_makes_the_probe_ready()
    {
        if (!OperatingSystem.IsLinux()
            || Environment.GetEnvironmentVariable("TRACKDUB_LINML_OPENVINO_INSTALL_E2E") != "1")
        {
            return;
        }

        var result = await new OpenVinoCatalogEpInstaller().EnsureInstalledAsync(new Progress<string>());
        WinMlCatalogReadinessReport report = await new Trackdub.Inference.Onnx.WinMlCatalog.OpenVinoCatalogReadinessProbe()
            .ProbeAsync(allowProviderDownloads: false);

        Assert.True(result.Succeeded, result.FailureDetail);
        Assert.True(report.IsReady, report.Detail);
    }

    [Fact]
    public async Task Installer_returns_the_catalog_reason_on_failure()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var installer = new OpenVinoCatalogEpInstaller(
            new StubProbe(LinuxReport(WinMlCatalogReadinessBlocker.EpDownloadFailed, ready: false, "download failed: 404")));

        var result = await installer.EnsureInstalledAsync(new Progress<string>());

        Assert.False(result.Succeeded);
        Assert.Equal("download failed: 404", result.FailureDetail);
    }
#endif

    private static WinMlCatalogReadinessReport LinuxReport(
        WinMlCatalogReadinessBlocker blocker,
        bool ready,
        string detail = "detail") =>
        new(
            OpenVinoCatalogProviderIds.LinMl,
            WinMlCatalogPlatformRoute.LinMlCatalog,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ready,
            IsRegisteredWithOrt: ready,
            Detail: detail);

    private sealed class StubProbe(WinMlCatalogReadinessReport report) : IOpenVinoCatalogReadinessProbe
    {
        public List<bool> AllowDownloadsCalls { get; } = [];

        public Task<WinMlCatalogReadinessReport> ProbeAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default)
        {
            AllowDownloadsCalls.Add(allowProviderDownloads);
            return Task.FromResult(report);
        }
    }
}
