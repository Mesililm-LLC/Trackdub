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

            try
            {
                BenchmarkModelCandidate candidate = modelPathResolver.ResolveSingle("kokoro-onnx");
                string? modelRootPath = candidate.RootDirectory ?? Path.GetDirectoryName(candidate.ModelPath);
                if (!string.IsNullOrWhiteSpace(modelRootPath))
                {
                    modelRootPath = KokoroVoiceCatalog.ResolveRootContainingVoices(modelRootPath);
                    if (!string.IsNullOrWhiteSpace(modelRootPath))
                    {
                        catalog = await KokoroVoiceCatalog.LoadAsync(modelRootPath).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning("Kokoro model path could not be resolved or catalog loaded; using known-available voices.", ex);
                catalog = KokoroVoiceCatalog.KnownAvailable();
            }

            initialized = true;
        }
        finally
        {
            initializeGate.Release();
        }
    }
}
