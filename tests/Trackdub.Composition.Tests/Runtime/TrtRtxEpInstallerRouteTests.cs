using Trackdub.Application.Runtime;
using Trackdub.Composition.Runtime;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class TrtRtxEpInstallerRouteTests
{
    [Fact]
    public async Task Catalog_route_skips_the_bundle_and_lets_the_bootstrap_download()
    {
        var bundle = new RecordingBundleInstaller();
        var bootstrap = new RecordingBootstrap(succeeded: true, "registered");
        var installer = new TrtRtxEpInstaller(bundle, bootstrap, useCatalogRoute: true);

        TrtRtxEpInstallResult result = await installer.EnsureInstalledAsync(new Progress<string>());

        Assert.True(result.Succeeded);
        Assert.Equal(0, bundle.Calls);
        Assert.Equal([true], bootstrap.AllowDownloadsCalls);
    }

    [Fact]
    public async Task Catalog_route_surfaces_the_bootstrap_failure()
    {
        var installer = new TrtRtxEpInstaller(
            new RecordingBundleInstaller(),
            new RecordingBootstrap(succeeded: false, "NVIDIA TensorRT RTX license not accepted."),
            useCatalogRoute: true);

        TrtRtxEpInstallResult result = await installer.EnsureInstalledAsync(new Progress<string>());

        Assert.False(result.Succeeded);
        Assert.Equal("NVIDIA TensorRT RTX license not accepted.", result.FailureDetail);
    }

    [Fact]
    public async Task Bundle_route_installs_the_bundle_then_registers_without_downloading()
    {
        var bundle = new RecordingBundleInstaller();
        var bootstrap = new RecordingBootstrap(succeeded: true, "registered");
        var installer = new TrtRtxEpInstaller(bundle, bootstrap, useCatalogRoute: false);

        TrtRtxEpInstallResult result = await installer.EnsureInstalledAsync(new Progress<string>());

        Assert.True(result.Succeeded);
        Assert.Equal(1, bundle.Calls);
        Assert.Equal([false], bootstrap.AllowDownloadsCalls);
    }

    [Fact]
    public async Task Bundle_route_stops_when_the_bundle_install_fails()
    {
        var bootstrap = new RecordingBootstrap(succeeded: true, "registered");
        var installer = new TrtRtxEpInstaller(
            new RecordingBundleInstaller(succeeded: false),
            bootstrap,
            useCatalogRoute: false);

        TrtRtxEpInstallResult result = await installer.EnsureInstalledAsync(new Progress<string>());

        Assert.False(result.Succeeded);
        Assert.Empty(bootstrap.AllowDownloadsCalls);
    }

    private sealed class RecordingBundleInstaller(bool succeeded = true) : ITrtRtxEpBundleInstaller
    {
        public int Calls { get; private set; }

        public Task<TrtRtxEpBundleInstallResult> EnsureBundleAsync(
            IProgress<string> progress,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new TrtRtxEpBundleInstallResult(succeeded, "/bundle", succeeded ? null : "bundle failed"));
        }
    }

    private sealed class RecordingBootstrap(bool succeeded, string detail) : ITensorRtRtxProviderBootstrap
    {
        public List<bool> AllowDownloadsCalls { get; } = [];

        public Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default)
        {
            AllowDownloadsCalls.Add(allowProviderDownloads);
            return Task.FromResult(new TensorRtRtxBootstrapResult(
                succeeded,
                TensorRtRtxProviderIds.PluginEpAbi,
                succeeded ? null : TensorRtRtxReadinessBlocker.EpDownloadFailed,
                detail));
        }
    }
}
