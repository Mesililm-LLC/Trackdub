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
    /// <summary>Whether the plugin is installed on disk (registration may still be pending).</summary>
    bool IsInstalled { get; }

    Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken);

    bool TryAppend(SessionOptions options, out string? failureReason);
}

/// <summary>
/// Used where LinML.Catalog is not referenced (the Windows TFM).
/// </summary>
internal sealed class UnavailableLinuxOpenVinoCatalog : ILinuxOpenVinoCatalog
{
    public static UnavailableLinuxOpenVinoCatalog Instance { get; } = new();

    private const string Reason = "The LinML catalog is not available in this build.";

    public bool IsInstalled => false;

    public Task<LinuxOpenVinoCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken) =>
        Task.FromResult(new LinuxOpenVinoCatalogResult(false, Reason));

    public bool TryAppend(SessionOptions options, out string? failureReason)
    {
        failureReason = Reason;
        return false;
    }
}
