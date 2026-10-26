using Trackdub.Composition.Tts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;

namespace Trackdub.Composition.Tests;

public sealed class KokoroVoiceCatalogHolderTests
{
    [Fact]
    public void Current_returns_known_available_catalog_before_initialization()
    {
        var holder = new KokoroVoiceCatalogHolder(
            new BenchmarkModelPathResolver(manifestRegistry: null, modelCacheDirectory: null),
            new NoopLogger());

        IVoiceCatalog catalog = holder.Current;

        Assert.NotEmpty(catalog.GetVoices());
        Assert.True(catalog.TryGetVoice("af_heart", out _));
    }

    [Fact]
    public async Task InitializeAsync_publishes_catalog_and_is_idempotent_under_concurrency()
    {
        var holder = new KokoroVoiceCatalogHolder(
            new BenchmarkModelPathResolver(manifestRegistry: null, modelCacheDirectory: null),
            new NoopLogger());

        Task[] initializers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                () => holder.InitializeAsync(TestContext.Current.CancellationToken),
                TestContext.Current.CancellationToken))
            .ToArray();
        Task[] readers = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(
                () => Assert.NotNull(holder.Current),
                TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(initializers.Concat(readers));

        IVoiceCatalog published = holder.Current;
        Assert.NotNull(published);

        await holder.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Same(published, holder.Current);
    }

    private sealed class NoopLogger : IApplicationLogger
    {
        public void LogDebug(string message) { }

        public void LogInformation(string message) { }

        public void LogWarning(string message, Exception? exception = null) { }

        public void LogError(string message, Exception? exception = null) { }
    }
}
