using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>Outcome of preparing the OpenVINO plugin EP through the Linux catalog.</summary>
public sealed record LinuxOpenVinoCatalogResult(bool IsReady, string Detail);

/// <summary>
/// Linux counterpart to the Windows ML catalog's OpenVINO EP: install, register and append the
/// pinned OpenVINO plugin execution provider.
/// </summary>
public interface ILinuxOpenVinoCatalog
{
    Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken);

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

    public Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken) =>
        Task.FromResult(new LinuxOpenVinoCatalogResult(false, Reason));

    public bool TryAppend(SessionOptions options, bool cpuOnly, out string? failureReason)
    {
        failureReason = Reason;
        return false;
    }
}
