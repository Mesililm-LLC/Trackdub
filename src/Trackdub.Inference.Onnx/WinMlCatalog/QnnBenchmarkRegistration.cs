#if WINDOWS
namespace Trackdub.Inference.Onnx.WinMlCatalog;

/// <summary>Registers an already-installed QNN catalog provider for benchmark processes.</summary>
public static class QnnBenchmarkRegistration
{
    public static bool TryRegisterInstalledProvider(out string? failureReason) =>
        WinMlCatalogSessionOptionsExtensions.TryRegisterInstalledQnnProvider(out failureReason);
}
#endif
