using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

namespace Trackdub.Inference.Tests;

public sealed class LinMlBuiltInProvidersTests
{
    [Fact]
    public void A_ready_built_in_provider_is_ready()
    {
        var catalog = new FakeCatalog(new FakeProvider(ProviderId.Cuda, ProviderSource.BuiltIn, ProviderReadyState.Ready));

        Assert.True(LinMlBuiltInProviders.IsReady(catalog, ProviderId.Cuda));
    }

    [Theory]
    [InlineData(ProviderReadyState.NotReady)]
    [InlineData(ProviderReadyState.NotPresent)]
    [InlineData(ProviderReadyState.Unsupported)]
    public void A_built_in_provider_that_is_not_ready_is_not_ready(ProviderReadyState state)
    {
        var catalog = new FakeCatalog(new FakeProvider(ProviderId.Tensorrt, ProviderSource.BuiltIn, state) { AppendWorks = false });

        Assert.False(LinMlBuiltInProviders.IsReady(catalog, ProviderId.Tensorrt));
    }

    [Fact]
    public void A_listed_provider_whose_append_probe_fails_is_not_ready()
    {
        var provider = new FakeProvider(ProviderId.Cuda, ProviderSource.BuiltIn, ProviderReadyState.Ready) { AppendWorks = false };

        Assert.False(LinMlBuiltInProviders.IsReady(new FakeCatalog(provider), ProviderId.Cuda));
        Assert.Equal(1, provider.EnsureCalls);
    }

    [Fact]
    public void An_unsupported_provider_is_not_probed()
    {
        var provider = new FakeProvider(ProviderId.Cuda, ProviderSource.BuiltIn, ProviderReadyState.Unsupported);

        Assert.False(LinMlBuiltInProviders.IsReady(new FakeCatalog(provider), ProviderId.Cuda));
        Assert.Equal(0, provider.EnsureCalls);
    }

    [Fact]
    public void A_plugin_provider_is_never_reported_as_a_ready_built_in()
    {
        var catalog = new FakeCatalog(new FakeProvider(ProviderId.Cuda, ProviderSource.Plugin, ProviderReadyState.Ready));

        Assert.False(LinMlBuiltInProviders.IsReady(catalog, ProviderId.Cuda));
    }

    [Fact]
    public void A_provider_the_catalog_does_not_know_is_not_ready()
    {
        Assert.False(LinMlBuiltInProviders.IsReady(new FakeCatalog(provider: null), ProviderId.Cuda));
    }

    private sealed class FakeCatalog(IExecutionProvider? provider) : IExecutionProviderCatalog
    {
        public IReadOnlyList<IExecutionProvider> FindAllProviders() => provider is null ? [] : [provider];

        public IExecutionProvider? Find(ProviderId id) => provider is not null && provider.Id == id ? provider : null;
    }

    private sealed class FakeProvider(ProviderId id, ProviderSource source, ProviderReadyState state) : IExecutionProvider
    {
        public ProviderId Id => id;

        public ProviderSource Source => source;

        public ProviderReadyState ReadyState => state;

        public int EnsureCalls { get; private set; }

        // Whether the append probe succeeds; independent of the cheap ReadyState.
        public bool AppendWorks { get; init; } = true;

        public string? LibraryPath => null;

        public string? PinnedVersion => null;

        public Task<ProviderEnsureResult> EnsureReadyAsync(EnsureOptions options, CancellationToken cancellationToken = default)
        {
            EnsureCalls++;
            Assert.False(options.AllowDownloads);
            return Task.FromResult(new ProviderEnsureResult(
                id,
                AppendWorks ? ProviderEnsureStatus.Ready : ProviderEnsureStatus.Failed,
                "probe",
                []));
        }

        public bool TryRegister(out string? failureReason)
        {
            failureReason = null;
            return false;
        }

        public IReadOnlyList<OrtEpDevice> GetDevices() => [];
    }
}
