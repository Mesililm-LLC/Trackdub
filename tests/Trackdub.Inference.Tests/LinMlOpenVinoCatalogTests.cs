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
    [InlineData(ProviderEnsureStatus.Ready, true)]
    [InlineData(ProviderEnsureStatus.Downloaded, true)]
    [InlineData(ProviderEnsureStatus.DownloadDisallowed, false)]
    [InlineData(ProviderEnsureStatus.NoDevices, false)]
    [InlineData(ProviderEnsureStatus.Failed, false)]
    public async Task EnsureReadyAsync_maps_catalog_status(ProviderEnsureStatus status, bool expectedReady)
    {
        var provider = new FakeProvider(_ => Task.FromResult(Result(status)));
        var catalog = new LinMlOpenVinoCatalog(new FakeCatalog(provider));

        LinuxOpenVinoCatalogResult result = await catalog.EnsureReadyAsync(allowDownloads: false, default);

        Assert.Equal(expectedReady, result.IsReady);
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
        Assert.False(catalog.IsInstalled);
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

    [Theory]
    [InlineData(ProviderReadyState.Ready, true)]
    [InlineData(ProviderReadyState.NotReady, true)]
    [InlineData(ProviderReadyState.NotPresent, false)]
    [InlineData(ProviderReadyState.Unsupported, false)]
    public void IsInstalled_follows_ready_state(ProviderReadyState state, bool expected)
    {
        var provider = new FakeProvider(_ => Task.FromResult(Result(ProviderEnsureStatus.Ready))) { ReadyState = state };

        Assert.Equal(expected, new LinMlOpenVinoCatalog(new FakeCatalog(provider)).IsInstalled);
    }

    [Fact]
    public void TryAppend_without_devices_fails_with_reason()
    {
        var provider = new FakeProvider(_ => Task.FromResult(Result(ProviderEnsureStatus.Ready)))
        {
            ReadyState = ProviderReadyState.NotPresent,
        };
        using var options = new SessionOptions();

        bool appended = new LinMlOpenVinoCatalog(new FakeCatalog(provider)).TryAppend(options, out string? reason);

        Assert.False(appended);
        Assert.False(string.IsNullOrWhiteSpace(reason));
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
