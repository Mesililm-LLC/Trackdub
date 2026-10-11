using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

public enum LinuxOpenVinoCatalogStatus
{
    /// <summary>Registered with ONNX Runtime and exposing at least one device.</summary>
    Ready,

    /// <summary>The plugin is not installed and downloads were not allowed.</summary>
    NotInstalled,

    /// <summary>Installed and registered, but the plugin exposes no device on this machine.</summary>
    NoDevices,

    /// <summary>Cannot work here: wrong OS/architecture, ORT too old, or no catalog support.</summary>
    Unsupported,

    /// <summary>Download, verification or registration failed, or preparation timed out.</summary>
    Failed,
}

/// <summary>Outcome of preparing the OpenVINO plugin EP through the Linux catalog.</summary>
public sealed record LinuxOpenVinoCatalogResult(LinuxOpenVinoCatalogStatus Status, string Detail)
{
    public bool IsReady => Status is LinuxOpenVinoCatalogStatus.Ready;
}

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
        Task.FromResult(new LinuxOpenVinoCatalogResult(LinuxOpenVinoCatalogStatus.Unsupported, Reason));

    public bool TryAppend(SessionOptions options, bool cpuOnly, out string? failureReason)
    {
        failureReason = Reason;
        return false;
    }
}
