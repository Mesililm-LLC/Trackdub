using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>
/// Linux counterpart to the Windows ML catalog's OpenVINO EP: install, register and append the
/// pinned OpenVINO plugin execution provider.
/// </summary>
public interface ILinuxOpenVinoCatalog
{
    Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken);

    /// <param name="cpuOnly">
    /// OpenVINO CPU-proxy mode: append only the CPU device, matching the session pool's host-RAM accounting.
    /// </param>
    bool TryAppend(SessionOptions options, bool cpuOnly, out string? failureReason);
}

/// <summary>
/// Used where LinML.Catalog is not referenced (the Windows TFM).
/// </summary>
internal sealed class UnavailableLinuxOpenVinoCatalog : ILinuxOpenVinoCatalog
{
    public static UnavailableLinuxOpenVinoCatalog Instance { get; } = new();

    private const string Reason = "The LinML catalog is not available in this build.";

    public Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken) =>
        Task.FromResult(new LinuxPluginCatalogResult(LinuxPluginCatalogStatus.Unsupported, Reason));

    public bool TryAppend(SessionOptions options, bool cpuOnly, out string? failureReason)
    {
        failureReason = Reason;
        return false;
    }
}
