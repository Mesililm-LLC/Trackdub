using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary><see cref="ILinuxOpenVinoCatalog"/> backed by <see cref="ExecutionProviderCatalog"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinMlOpenVinoCatalog : ILinuxOpenVinoCatalog
{
    // NPU first, matching WinMlCatalogSessionOptionsExtensions.TryAppendOpenVinoCatalogProvider.
    private static readonly OrtHardwareDeviceType[] AcceleratorFirst =
        [OrtHardwareDeviceType.NPU, OrtHardwareDeviceType.GPU, OrtHardwareDeviceType.CPU];

    private static readonly OrtHardwareDeviceType[] CpuOnly = [OrtHardwareDeviceType.CPU];

    internal static IReadOnlyList<OrtHardwareDeviceType> PreferredDeviceTypes(bool cpuOnly) =>
        cpuOnly ? CpuOnly : AcceleratorFirst;

    public static LinMlOpenVinoCatalog Default { get; } = new(ExecutionProviderCatalog.GetDefault());

    private readonly LinMlPluginCatalog _plugin;

    public LinMlOpenVinoCatalog(IExecutionProviderCatalog catalog)
    {
        _plugin = new LinMlPluginCatalog(catalog, ProviderId.OpenVino, "OpenVINO");
    }

    public Task<LinuxPluginCatalogResult> EnsureReadyAsync(
        bool allowDownloads,
        CancellationToken cancellationToken) =>
        _plugin.EnsureReadyAsync(allowDownloads, cancellationToken);

    public bool TryAppend(SessionOptions options, bool cpuOnly, out string? failureReason) =>
        _plugin.TryAppend(options, PreferredDeviceTypes(cpuOnly), out failureReason);
}
