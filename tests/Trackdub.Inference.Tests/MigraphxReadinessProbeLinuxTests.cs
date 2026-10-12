using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Onnx.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class MigraphxReadinessProbeLinuxTests
{
    [Fact]
    public async Task Without_amd_gpu_reports_vendor_mismatch_and_skips_the_catalog()
    {
        var catalog = new StubCatalog(LinuxPluginCatalogStatus.Ready);
        var probe = new MigraphxReadinessProbe(new StubRuntimeProbe(amdGpu: false), catalog);

        MigraphxReadinessReport report = await probe.ProbeAsync(allowProviderDownloads: true);

        Assert.Equal(MigraphxPlatformRoute.LinMlCatalog, report.Route);
        Assert.Equal(MigraphxReadinessBlocker.GpuVendorMismatch, report.Blocker);
        Assert.False(report.IsReady);
        Assert.Empty(catalog.EnsureCalls);
    }

    [Fact]
    public async Task Built_in_ort_provider_is_ready_without_the_catalog()
    {
        var catalog = new StubCatalog(LinuxPluginCatalogStatus.NotInstalled);
        var probe = new MigraphxReadinessProbe(new StubRuntimeProbe(amdGpu: true, ortListed: true), catalog);

        MigraphxReadinessReport report = await probe.ProbeAsync(allowProviderDownloads: false);

        Assert.True(report.IsReady);
        Assert.Equal(MigraphxPlatformRoute.NativeRocm, report.Route);
        Assert.Equal(MigraphxProviderIds.Rocm, report.ProviderId);
        Assert.Empty(catalog.EnsureCalls);
    }

    [Fact]
    public async Task Ready_catalog_plugin_is_ready_on_the_linml_route()
    {
        var catalog = new StubCatalog(LinuxPluginCatalogStatus.Ready);
        var probe = new MigraphxReadinessProbe(new StubRuntimeProbe(amdGpu: true), catalog);

        MigraphxReadinessReport report = await probe.ProbeAsync(allowProviderDownloads: false);

        Assert.True(report.IsReady);
        Assert.Equal(MigraphxPlatformRoute.LinMlCatalog, report.Route);
        Assert.Equal(MigraphxProviderIds.LinMl, report.ProviderId);
        Assert.Equal([false], catalog.EnsureCalls);
    }

    [Theory]
    [InlineData(LinuxPluginCatalogStatus.NotInstalled, false, MigraphxReadinessBlocker.EpNotPresent)]
    [InlineData(LinuxPluginCatalogStatus.Failed, true, MigraphxReadinessBlocker.EpDownloadFailed)]
    [InlineData(LinuxPluginCatalogStatus.Failed, false, MigraphxReadinessBlocker.EpRegisterFailed)]
    [InlineData(LinuxPluginCatalogStatus.NoDevices, false, MigraphxReadinessBlocker.EpRegisterFailed)]
    [InlineData(LinuxPluginCatalogStatus.Unsupported, true, MigraphxReadinessBlocker.OrtProviderUnavailable)]
    public async Task Catalog_status_maps_to_blocker(
        LinuxPluginCatalogStatus status,
        bool allowDownloads,
        MigraphxReadinessBlocker expected)
    {
        var catalog = new StubCatalog(status);
        var probe = new MigraphxReadinessProbe(new StubRuntimeProbe(amdGpu: true), catalog);

        MigraphxReadinessReport report = await probe.ProbeAsync(allowDownloads);

        Assert.False(report.IsReady);
        Assert.Equal(MigraphxPlatformRoute.LinMlCatalog, report.Route);
        Assert.Equal(expected, report.Blocker);
        Assert.True(report.IsHardwareEligible);
        Assert.Equal("stub detail", report.Detail);
        Assert.Equal([allowDownloads], catalog.EnsureCalls);
    }

    private sealed class StubCatalog(LinuxPluginCatalogStatus status) : ILinuxMigraphxCatalog
    {
        public List<bool> EnsureCalls { get; } = [];

        public Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken)
        {
            EnsureCalls.Add(allowDownloads);
            return Task.FromResult(new LinuxPluginCatalogResult(status, "stub detail"));
        }
    }

    private sealed class StubRuntimeProbe(bool amdGpu, bool ortListed = false) : ILinuxNativeGpuRuntimeProbe
    {
        public bool IsNvidiaDriverLoaded() => false;

        public bool IsAmdGpuPresent() => amdGpu;

        public bool IsNativeTensorRtAvailable() => false;

        public bool IsCudaOrtProviderAvailable() => false;

        public bool IsMigraphxOrtProviderAvailable() => ortListed;
    }
}
