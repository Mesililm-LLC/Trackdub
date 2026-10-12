using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

namespace Trackdub.Inference.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinMlMigraphxCatalogTests
{
    [Fact]
    public async Task EnsureReadyAsync_without_amd_gpu_never_touches_the_catalog()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Ready);
        var catalog = new LinMlMigraphxCatalog(new FakeCatalog(provider), isAmdGpuPresent: () => false, isRuntimePresent: () => true);

        LinuxPluginCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: true, default);

        Assert.Equal(LinuxPluginCatalogStatus.Unsupported, result.Status);
        Assert.Contains("AMD GPU", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public async Task EnsureReadyAsync_without_rocm_runtime_never_downloads()
    {
        var provider = new FakeProvider(ProviderEnsureStatus.Downloaded);
        var catalog = new LinMlMigraphxCatalog(new FakeCatalog(provider), isAmdGpuPresent: () => true, isRuntimePresent: () => false);

        LinuxPluginCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: true, default);

        Assert.Equal(LinuxPluginCatalogStatus.Unsupported, result.Status);
        Assert.Contains("libmigraphx_c.so.3", result.Detail, StringComparison.Ordinal);
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Theory]
    [InlineData(ProviderEnsureStatus.Ready, LinuxPluginCatalogStatus.Ready)]
    [InlineData(ProviderEnsureStatus.Downloaded, LinuxPluginCatalogStatus.Ready)]
    [InlineData(ProviderEnsureStatus.DownloadDisallowed, LinuxPluginCatalogStatus.NotInstalled)]
    [InlineData(ProviderEnsureStatus.NoDevices, LinuxPluginCatalogStatus.NoDevices)]
    [InlineData(ProviderEnsureStatus.Failed, LinuxPluginCatalogStatus.Failed)]
    public async Task EnsureReadyAsync_with_amd_gpu_and_runtime_uses_the_migraphx_plugin(
        ProviderEnsureStatus status,
        LinuxPluginCatalogStatus expected)
    {
        var provider = new FakeProvider(status);
        var catalog = new LinMlMigraphxCatalog(new FakeCatalog(provider), isAmdGpuPresent: () => true, isRuntimePresent: () => true);

        LinuxPluginCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: true, default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(1, provider.EnsureCalls);
        Assert.True(provider.LastOptions?.AllowDownloads);
        Assert.Contains("MIGraphX plugin EP", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureReadyAsync_reports_missing_provider()
    {
        var catalog = new LinMlMigraphxCatalog(new FakeCatalog(provider: null), () => true, () => true);

        LinuxPluginCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: false, default);

        Assert.Equal(LinuxPluginCatalogStatus.Unsupported, result.Status);
    }

    private sealed class FakeCatalog(IExecutionProvider? provider) : IExecutionProviderCatalog
    {
        public IReadOnlyList<IExecutionProvider> FindAllProviders() => provider is null ? [] : [provider];

        public IExecutionProvider? Find(ProviderId id) => id == ProviderId.MigraphX ? provider : null;
    }

    private sealed class FakeProvider(ProviderEnsureStatus status) : IExecutionProvider
    {
        public int EnsureCalls { get; private set; }

        public EnsureOptions? LastOptions { get; private set; }

        public ProviderId Id => ProviderId.MigraphX;

        public ProviderSource Source => ProviderSource.Plugin;

        public ProviderReadyState ReadyState => ProviderReadyState.NotPresent;

        public string? LibraryPath => null;

        public string? PinnedVersion => "2611.1";

        public Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            LastOptions = options;
            return Task.FromResult(new ProviderEnsureResult(ProviderId.MigraphX, status, "detail", []));
        }

        public bool TryRegister(out string? failureReason)
        {
            failureReason = "not supported in tests";
            return false;
        }

        public IReadOnlyList<OrtEpDevice> GetDevices() => [];
    }
}
