using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinMlTensorRtRtxBootstrapTests
{
    [Fact]
    public async Task Without_an_nvidia_driver_the_catalog_is_never_touched()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Downloaded);
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, nvidiaDriverLoaded: false);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.False(result.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.GpuVendorMismatch, result.Blocker);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public async Task A_usable_user_directory_wins_and_never_downloads()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Downloaded);
        var directory = new RecordingBootstrap();
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, directoryBootstrap: directory, overrideUsable: true, licenseAccepted: true);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.True(result.Succeeded);
        Assert.Equal([false], directory.AllowDownloadsCalls);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public async Task An_unusable_user_directory_falls_through_to_the_catalog()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Ready);
        var directory = new RecordingBootstrap();
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, directoryBootstrap: directory, overrideUsable: false);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.True(result.Succeeded);
        Assert.Empty(directory.AllowDownloadsCalls);
        Assert.Equal(1, provider.EnsureCalls);
    }

    [Fact]
    public async Task Downloads_need_the_license_even_when_the_caller_allows_them()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.DownloadDisallowed);
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, licenseAccepted: false);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.False(result.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpDownloadFailed, result.Blocker);
        Assert.Equal(LinMlTensorRtRtxBootstrap.LicenseNotAcceptedDetail, result.Detail);
        Assert.False(provider.LastOptions?.AllowDownloads);
    }

    [Fact]
    public async Task A_licensed_install_downloads_through_the_catalog()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Downloaded);
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, licenseAccepted: true);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.True(result.Succeeded);
        Assert.Equal(TensorRtRtxProviderIds.PluginEpAbi, result.ProviderId);
        Assert.True(provider.LastOptions?.AllowDownloads);
        Assert.Contains("TensorRT-RTX plugin EP", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_download_request_a_licensed_host_still_does_not_download()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.DownloadDisallowed);
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, licenseAccepted: true);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.False(result.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpNotPresent, result.Blocker);
        Assert.False(provider.LastOptions?.AllowDownloads);
    }

    [Theory]
    [InlineData(ProviderEnsureStatus.NoDevices, TensorRtRtxReadinessBlocker.OrtProviderUnavailable)]
    [InlineData(ProviderEnsureStatus.AbiIncompatible, TensorRtRtxReadinessBlocker.PlatformUnsupported)]
    [InlineData(ProviderEnsureStatus.NotAvailable, TensorRtRtxReadinessBlocker.PlatformUnsupported)]
    [InlineData(ProviderEnsureStatus.Failed, TensorRtRtxReadinessBlocker.EpRegisterFailed)]
    public async Task Catalog_failures_map_to_readiness_blockers(
        ProviderEnsureStatus status,
        TensorRtRtxReadinessBlocker expected)
    {
        var provider = new FakeProvider(status);
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, licenseAccepted: true);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Blocker);
    }

    [Fact]
    public async Task A_failed_licensed_download_is_reported_as_a_download_failure()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Failed);
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider, licenseAccepted: true);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.Equal(TensorRtRtxReadinessBlocker.EpDownloadFailed, result.Blocker);
    }

    [Fact]
    public async Task A_catalog_without_the_provider_is_unsupported()
    {
        LinMlTensorRtRtxBootstrap bootstrap = Make(provider: null);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.False(result.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.PlatformUnsupported, result.Blocker);
    }

    [Fact]
    public async Task A_plugin_already_registered_in_the_process_is_reported_ready_without_touching_anything()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Downloaded);
        var directory = new RecordingBootstrap();
        LinMlTensorRtRtxBootstrap bootstrap = Make(
            provider,
            licenseAccepted: true,
            overrideUsable: true,
            directoryBootstrap: directory,
            alreadyRegistered: true);

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.True(result.Succeeded);
        Assert.Contains("already registered", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, provider.EnsureCalls);
        Assert.Empty(directory.AllowDownloadsCalls);
    }

    [Fact]
    public async Task A_vendored_WSL2_build_without_stock_libraries_reports_missing_when_downloads_are_off()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Ready);
        LinMlTensorRtRtxBootstrap bootstrap = Make(
            provider,
            licenseAccepted: true,
            wsl2Plugin: LinMlWsl2TensorRtRtxPluginTests.Wsl(devices: 0, stockDirectoryPresent: false));

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.False(result.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpNotPresent, result.Blocker);
        Assert.Contains("Restart", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public async Task A_vendored_WSL2_build_without_stock_libraries_installs_the_stock_plugin_when_downloads_are_allowed()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.NoDevices);
        LinMlTensorRtRtxBootstrap bootstrap = Make(
            provider,
            licenseAccepted: true,
            wsl2Plugin: LinMlWsl2TensorRtRtxPluginTests.Wsl(devices: 0, stockDirectoryPresent: false));

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.Equal(1, provider.EnsureCalls);
        Assert.True(provider.LastOptions?.AllowDownloads);
        Assert.False(result.Succeeded);
        Assert.Contains("Restart", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registered_WSL2_build_is_used_without_touching_the_catalog()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Downloaded);
        LinMlTensorRtRtxBootstrap bootstrap = Make(
            provider,
            licenseAccepted: true,
            wsl2Plugin: LinMlWsl2TensorRtRtxPluginTests.Wsl(devices: 1));

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: true);

        Assert.True(result.Succeeded);
        Assert.Contains("WSL2", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public async Task A_failed_WSL2_build_is_reported_instead_of_falling_back_to_the_stock_plugin()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Ready);
        LinMlTensorRtRtxBootstrap bootstrap = Make(
            provider,
            wsl2Plugin: LinMlWsl2TensorRtRtxPluginTests.WslRegistrationFails());

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.False(result.Succeeded);
        Assert.Equal(TensorRtRtxReadinessBlocker.EpRegisterFailed, result.Blocker);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public async Task Stock_plugin_with_no_devices_on_WSL2_points_at_the_WSL2_build()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.NoDevices);
        LinMlTensorRtRtxBootstrap bootstrap = Make(
            provider,
            wsl2Plugin: LinMlWsl2TensorRtRtxPluginTests.Wsl(devices: 0, patchedPresent: false));

        TensorRtRtxBootstrapResult result = await bootstrap.EnsureRegisteredAsync(allowProviderDownloads: false);

        Assert.Equal(TensorRtRtxReadinessBlocker.OrtProviderUnavailable, result.Blocker);
        Assert.Contains("trt-rtx-wsl2", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void The_factory_selects_the_catalog_bootstrap_on_Linux()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        ITensorRtRtxProviderBootstrap created = TensorRtRtxProviderBootstrapFactory.Create(
            static _ => ValueTask.FromResult<string?>(null),
            static _ => ValueTask.FromResult<string?>(null));

        Assert.IsType<LinMlTensorRtRtxBootstrap>(created);
    }

    private static LinMlTensorRtRtxBootstrap Make(
        FakeProvider? provider,
        bool nvidiaDriverLoaded = true,
        bool licenseAccepted = false,
        bool overrideUsable = false,
        ITensorRtRtxProviderBootstrap? directoryBootstrap = null,
        LinMlWsl2TensorRtRtxPlugin? wsl2Plugin = null,
        bool alreadyRegistered = false) =>
        new(
            new FakeCatalog(provider),
            directoryBootstrap ?? new RecordingBootstrap(),
            static _ => ValueTask.FromResult<string?>(null),
            _ => ValueTask.FromResult(licenseAccepted),
            () => nvidiaDriverLoaded
                ? (true, TensorRtRtxReadinessBlocker.None, "driver")
                : (false, TensorRtRtxReadinessBlocker.GpuVendorMismatch, "No loaded NVIDIA driver detected for TensorRT RTX."),
            _ => overrideUsable,
            wsl2Plugin ?? LinMlWsl2TensorRtRtxPluginTests.NotWsl(),
            () => alreadyRegistered);

    private sealed class RecordingBootstrap : ITensorRtRtxProviderBootstrap
    {
        public List<bool> AllowDownloadsCalls { get; } = [];

        public Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
            bool allowProviderDownloads,
            CancellationToken cancellationToken = default)
        {
            AllowDownloadsCalls.Add(allowProviderDownloads);
            return Task.FromResult(new TensorRtRtxBootstrapResult(
                Succeeded: true,
                TensorRtRtxProviderIds.PluginEpAbi,
                Blocker: null,
                "registered from the user directory"));
        }
    }

    private sealed class FakeCatalog(IExecutionProvider? provider) : IExecutionProviderCatalog
    {
        public IReadOnlyList<IExecutionProvider> FindAllProviders() => provider is null ? [] : [provider];

        public IExecutionProvider? Find(ProviderId id) => id == ProviderId.TensorrtRtx ? provider : null;
    }

    private sealed class FakeProvider(ProviderEnsureStatus status) : IExecutionProvider
    {
        public int EnsureCalls { get; private set; }

        public EnsureOptions? LastOptions { get; private set; }

        public ProviderId Id => ProviderId.TensorrtRtx;

        public ProviderSource Source => ProviderSource.Plugin;

        public ProviderReadyState ReadyState => ProviderReadyState.NotPresent;

        public string? LibraryPath => null;

        public string? PinnedVersion => "0.4.0";

        public Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            LastOptions = options;
            return Task.FromResult(new ProviderEnsureResult(ProviderId.TensorrtRtx, status, "detail", []));
        }

        public bool TryRegister(out string? failureReason)
        {
            failureReason = "not supported in tests";
            return false;
        }

        public IReadOnlyList<OrtEpDevice> GetDevices() => [];
    }
}
