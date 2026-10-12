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
/// Whether the host has a loadable ROCm MIGraphX runtime the plugin can use
/// (<c>libmigraphx_c.so.3</c>, MIGraphX 2.17 or newer).
/// </summary>
public static class LinuxMigraphxRuntimeProbe
{
    // Matches the plugin's RUNPATH ($ORIGIN:/opt/rocm/lib); the loader fallback covers ROCm
    // installs on the regular library path.
    private const string RocmLibraryPath = "/opt/rocm/lib/" + MigraphxProviderConstants.LinuxRuntimeLibrary;

    // MIGraphX 2.15 ships the same libmigraphx_c.so.3 soname but lacks this export, which the
    // plugin calls, so a successful load alone does not prove the version.
    internal const string RequiredExport = "migraphx_compile_options_set_advance_backend_options";

    public static bool IsRuntimePresent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        return HasRequiredExport(RocmLibraryPath)
            || HasRequiredExport(MigraphxProviderConstants.LinuxRuntimeLibrary);
    }

    private static bool HasRequiredExport(string library)
    {
        if (!NativeLibrary.TryLoad(library, out IntPtr handle))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(handle, RequiredExport, out _);
        }
        finally
        {
            NativeLibrary.Free(handle);
        }
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
