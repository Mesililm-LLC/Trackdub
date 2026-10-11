using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

namespace Trackdub.Inference.Tests;

[SupportedOSPlatform("linux")]
public sealed class LinMlOpenVinoCatalogTests
{
    [Theory]
    [InlineData(ProviderEnsureStatus.Ready, LinuxOpenVinoCatalogStatus.Ready)]
    [InlineData(ProviderEnsureStatus.Downloaded, LinuxOpenVinoCatalogStatus.Ready)]
    [InlineData(ProviderEnsureStatus.DownloadDisallowed, LinuxOpenVinoCatalogStatus.NotInstalled)]
    [InlineData(ProviderEnsureStatus.NoDevices, LinuxOpenVinoCatalogStatus.NoDevices)]
    [InlineData(ProviderEnsureStatus.AbiIncompatible, LinuxOpenVinoCatalogStatus.Unsupported)]
    [InlineData(ProviderEnsureStatus.NotAvailable, LinuxOpenVinoCatalogStatus.Unsupported)]
    [InlineData(ProviderEnsureStatus.Failed, LinuxOpenVinoCatalogStatus.Failed)]
    public async Task EnsureReadyAsync_maps_catalog_status(ProviderEnsureStatus status, LinuxOpenVinoCatalogStatus expected)
    {
        var provider = new FakeProvider(_ => Task.FromResult(Result(status)));
        var catalog = new LinMlOpenVinoCatalog(new FakeCatalog(provider));

        LinuxOpenVinoCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: false, default);

        Assert.Equal(expected, result.Status);
        Assert.Equal(expected is LinuxOpenVinoCatalogStatus.Ready, result.IsReady);
        Assert.Contains(status.ToString(), result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EnsureReadyAsync_passes_allowDownloads(bool allowDownloads)
    {
        EnsureOptions? seen = null;
        var provider = new FakeProvider(options =>
        {
            seen = options;
            return Task.FromResult(Result(ProviderEnsureStatus.Ready));
        });

        await new LinMlOpenVinoCatalog(new FakeCatalog(provider)).EnsureReadyAsync(allowDownloads, default);

        Assert.Equal(allowDownloads, seen?.AllowDownloads);
    }

    [Fact]
    public async Task EnsureReadyAsync_reports_missing_provider()
    {
        var catalog = new LinMlOpenVinoCatalog(new FakeCatalog(provider: null));

        LinuxOpenVinoCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: true, default);

        Assert.False(result.IsReady);
    }

    [Fact]
    public async Task EnsureReadyAsync_propagates_caller_cancellation()
    {
        var provider = new FakeProvider(_ => Task.FromResult(Result(ProviderEnsureStatus.Ready)), honorToken: true);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LinMlOpenVinoCatalog(new FakeCatalog(provider)).EnsureReadyAsync(allowDownloads: false, cts.Token));
    }

    [Fact]
    public void TryAppend_without_devices_fails_with_reason()
    {
        var provider = new FakeProvider(_ => Task.FromResult(Result(ProviderEnsureStatus.Ready)))
        {
            ReadyState = ProviderReadyState.NotPresent,
        };
        using var options = new SessionOptions();

        bool appended = new LinMlOpenVinoCatalog(new FakeCatalog(provider)).TryAppend(options, cpuOnly: false, out string? reason);

        Assert.False(appended);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void Cpu_proxy_mode_prefers_only_the_cpu_device()
    {
        Assert.Equal([OrtHardwareDeviceType.CPU], LinMlOpenVinoCatalog.PreferredDeviceTypes(cpuOnly: true));
        Assert.Equal(
            [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU, OrtHardwareDeviceType.CPU],
            LinMlOpenVinoCatalog.PreferredDeviceTypes(cpuOnly: false));
    }

    private static ProviderEnsureResult Result(ProviderEnsureStatus status) =>
        new(ProviderId.OpenVino, status, "detail", []);

    private sealed class FakeCatalog(IExecutionProvider? provider) : IExecutionProviderCatalog
    {
        public IReadOnlyList<IExecutionProvider> FindAllProviders() => provider is null ? [] : [provider];

        public IExecutionProvider? Find(ProviderId id) => id == ProviderId.OpenVino ? provider : null;
    }

    private sealed class FakeProvider(
        Func<EnsureOptions, Task<ProviderEnsureResult>> ensure,
        bool honorToken = false) : IExecutionProvider
    {
        public ProviderId Id => ProviderId.OpenVino;

        public ProviderSource Source => ProviderSource.Plugin;

        public ProviderReadyState ReadyState { get; init; } = ProviderReadyState.NotReady;

        public string? LibraryPath => null;

        public string? PinnedVersion => "test";

        public Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default)
        {
            if (honorToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            return ensure(options);
        }

        public bool TryRegister(out string? failureReason)
        {
            failureReason = "not supported in tests";
            return false;
        }

        public IReadOnlyList<OrtEpDevice> GetDevices() => [];
    }
}
