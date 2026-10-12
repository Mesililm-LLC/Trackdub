using System.Collections.Concurrent;
using LinML.Catalog;
using LinML.Contracts;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>
/// Readiness of the execution providers compiled into the loaded ONNX Runtime (CUDA, classic
/// TensorRT) as the LinML catalog sees them. Ready means ORT lists the provider and appending it
/// to session options works, so a CPU-only ORT build or a missing CUDA library reports not ready
/// instead of passing on the strength of a loaded NVIDIA driver alone.
/// </summary>
internal static class LinMlBuiltInProviders
{
    // The append probe builds session options; cache the default catalog's answer per provider.
    // Lazy<bool> runs the probe once even under concurrent first calls (GetOrAdd's factory runs
    // outside its lock). A thrown probe is cached and rethrown on later reads, unlike GetOrAdd.
    private static readonly ConcurrentDictionary<ProviderId, Lazy<bool>> DefaultCatalogResults = new();

    public static bool IsReady(ProviderId id) =>
        DefaultCatalogResults.GetOrAdd(id, static key => new Lazy<bool>(() => IsReady(ExecutionProviderCatalog.GetDefault(), key))).Value;

    internal static bool IsReady(IExecutionProviderCatalog catalog, ProviderId id)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(id);

        if (catalog.Find(id) is not { Source: ProviderSource.BuiltIn } provider
            || provider.ReadyState is ProviderReadyState.Unsupported)
        {
            return false;
        }

        // ReadyState reports Ready as soon as ORT lists an EP device, before any append is tried.
        // EnsureReadyAsync on a built-in runs the append probe (it never downloads), so ready means
        // the provider can actually be appended to session options.
        return provider.EnsureReadyAsync(new EnsureOptions(AllowDownloads: false)).GetAwaiter().GetResult().IsReady;
    }
}
