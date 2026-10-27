using Trackdub.Application.Dubbing;
using Trackdub.Composition.Pipeline;
using Trackdub.Contracts;
using Trackdub.Licensing;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Thread-safe factory that creates per-project <see cref="IDubbingSession"/> instances
/// from a root <see cref="ServiceProvider"/> built by headless composition.
/// </summary>
public sealed class HeadlessDubbingSessionFactory : IDubbingSessionFactory
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly Trackdub.Contracts.Benchmarking.IProcessGpuMemoryReader? _processGpuReader;
    private readonly IReadOnlyDictionary<int, long>? _adapterLuidMap;
    private volatile bool _disposed;

    public HeadlessDubbingSessionFactory(ServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));

        // Centralize eager activation here so every headless host path—including advanced
        // callers that build a provider from AddHeadlessTrackdub directly—applies custom
        // storage environment values before any session or static consumer can run. The
        // resolved singleton remains owned and disposed by this provider.
        serviceProvider.GetService<HeadlessStorageEnvironmentScope>();

        // Same centralization for the engine auto-warm installer: hosts that run a
        // generic host get it as an IHostedService, and TrackdubBuilder starts it
        // explicitly, but an advanced host constructing this factory directly would
        // otherwise skip license initialization, voice-catalog loading, and the
        // EP-context hardware identity. StartAsync is idempotent (the holder and
        // license initializer guard re-entry), so hosts that already started it
        // observe no second effect.
        serviceProvider.GetService<EngineAutoWarmInstaller>()
            ?.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

        _scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _processGpuReader = HeadlessCompositionRoot.BindSharedPoolProcessGpuAdmission(serviceProvider);
        _adapterLuidMap = HeadlessCompositionRoot.BindSharedPoolAdapterLuidMap(serviceProvider);
    }

    /// <inheritdoc />
    public IDubbingSession CreateSession(string projectRootPath, StudioSettings? settings = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);

        IServiceScope scope = _scopeFactory.CreateScope();
        try
        {
            return new HeadlessDubbingSession(scope, projectRootPath, settings);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        HeadlessCompositionRoot.ClearSharedPoolProcessGpuAdmission(_processGpuReader);
        HeadlessCompositionRoot.ClearSharedPoolAdapterLuidMap(_adapterLuidMap);
        _serviceProvider.Dispose();
    }
}
