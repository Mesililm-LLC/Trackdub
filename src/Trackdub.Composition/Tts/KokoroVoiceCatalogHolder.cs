using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Infrastructure.Logging;

namespace Trackdub.Composition.Tts;

/// <summary>
/// Holds the process-wide Kokoro catalog, initialized asynchronously during host startup.
/// Scope resolution reads the current snapshot and never performs filesystem enumeration.
/// </summary>
public sealed class KokoroVoiceCatalogHolder(
    BenchmarkModelPathResolver modelPathResolver,
    IApplicationLogger logger)
{
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    private IVoiceCatalog catalog = KokoroVoiceCatalog.KnownAvailable();
    private volatile bool initialized;

    public IVoiceCatalog Current => Volatile.Read(ref catalog);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (initialized)
        {
            return;
        }

        await initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized)
            {
                return;
            }

            await LoadCatalogCoreAsync().ConfigureAwait(false);

            initialized = true;
        }
        finally
        {
            initializeGate.Release();
        }
    }

    /// <summary>
    /// Re-reads the voice catalog from disk, replacing the snapshot even when already
    /// initialized. Called after a model install/uninstall that may have added or removed
    /// voice packs (e.g. a delta install): without this the singleton would serve
    /// pre-install state until the next process restart.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await LoadCatalogCoreAsync().ConfigureAwait(false);

            initialized = true;
        }
        finally
        {
            initializeGate.Release();
        }
    }

    /// <summary>
    /// Drops the initialized flag so the next <see cref="InitializeAsync"/> reloads from
    /// disk. A cheap synchronous alternative to <see cref="RefreshAsync"/> for callers
    /// that cannot await; the catalog itself is replaced on the next initialization.
    /// </summary>
    public void Invalidate() => initialized = false;

    private async Task LoadCatalogCoreAsync()
    {
        try
        {
            BenchmarkModelCandidate candidate = modelPathResolver.ResolveSingle("kokoro-onnx");
            string? modelRootPath = candidate.RootDirectory ?? Path.GetDirectoryName(candidate.ModelPath);
            if (!string.IsNullOrWhiteSpace(modelRootPath))
            {
                modelRootPath = KokoroVoiceCatalog.ResolveRootContainingVoices(modelRootPath);
                if (!string.IsNullOrWhiteSpace(modelRootPath))
                {
                    IVoiceCatalog loadedCatalog = await KokoroVoiceCatalog.LoadAsync(modelRootPath).ConfigureAwait(false);
                    Volatile.Write(ref catalog, loadedCatalog);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Kokoro model path could not be resolved or catalog loaded; using known-available voices.", ex);
            Volatile.Write(ref catalog, KokoroVoiceCatalog.KnownAvailable());
        }
    }
}
