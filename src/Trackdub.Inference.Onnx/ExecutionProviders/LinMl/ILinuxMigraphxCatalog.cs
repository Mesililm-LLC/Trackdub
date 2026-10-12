using System.Runtime.InteropServices;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>
/// Linux counterpart to the Windows ML catalog's MIGraphX EP: install and register AMD's pinned
/// MIGraphX plugin execution provider. Once registered, the plugin's devices are what
/// <c>MigraphxSessionOptionsExtensions</c> appends.
/// </summary>
public interface ILinuxMigraphxCatalog
{
    Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken);
}

/// <summary>
/// Whether the host has the ROCm MIGraphX runtime the plugin links against
/// (<c>libmigraphx_c.so.3</c>, MIGraphX 2.17 or newer).
/// </summary>
public static class LinuxMigraphxRuntimeProbe
{
    // Matches the plugin's RUNPATH ($ORIGIN:/opt/rocm/lib); the loader fallback covers ROCm
    // installs on the regular library path.
    private const string RocmLibraryPath = "/opt/rocm/lib/" + MigraphxProviderConstants.LinuxRuntimeLibrary;

    public static bool IsRuntimePresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (File.Exists(RocmLibraryPath))
        {
            return true;
        }

        if (NativeLibrary.TryLoad(MigraphxProviderConstants.LinuxRuntimeLibrary, out IntPtr handle))
        {
            NativeLibrary.Free(handle);
            return true;
        }

        return false;
    }
}

/// <summary>
/// Used where LinML.Catalog is not referenced (the Windows TFM).
/// </summary>
internal sealed class UnavailableLinuxMigraphxCatalog : ILinuxMigraphxCatalog
{
    public static UnavailableLinuxMigraphxCatalog Instance { get; } = new();

    public Task<LinuxPluginCatalogResult> EnsureReadyAsync(bool allowDownloads, CancellationToken cancellationToken) =>
        Task.FromResult(new LinuxPluginCatalogResult(
            LinuxPluginCatalogStatus.Unsupported,
            "The LinML catalog is not available in this build."));
}
