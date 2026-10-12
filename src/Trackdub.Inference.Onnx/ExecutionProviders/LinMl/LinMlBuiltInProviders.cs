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
    public static bool IsReady(ProviderId id) => IsReady(ExecutionProviderCatalog.GetDefault(), id);

    internal static bool IsReady(IExecutionProviderCatalog catalog, ProviderId id)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(id);

        // ReadyState is cheap and cached after the first append probe; it never downloads.
        return catalog.Find(id) is { Source: ProviderSource.BuiltIn } provider
            && provider.ReadyState is ProviderReadyState.Ready;
    }
}
