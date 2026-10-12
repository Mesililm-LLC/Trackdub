using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary><see cref="ILinuxMigraphxCatalog"/> backed by <see cref="ExecutionProviderCatalog"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinMlMigraphxCatalog : ILinuxMigraphxCatalog
{
    public static LinMlMigraphxCatalog Default { get; } = new(
        ExecutionProviderCatalog.GetDefault(),
        new LinuxNativeGpuRuntimeProbe().IsAmdGpuPresent,
        LinuxMigraphxRuntimeProbe.IsRuntimePresent);

    private readonly LinMlPluginCatalog _plugin;
    private readonly Func<bool> _isAmdGpuPresent;
    private readonly Func<bool> _isRuntimePresent;

    public LinMlMigraphxCatalog(
        IExecutionProviderCatalog catalog,
        Func<bool> isAmdGpuPresent,
        Func<bool> isRuntimePresent)
    {
        _plugin = new LinMlPluginCatalog(catalog, ProviderId.MigraphX, "MIGraphX");
        _isAmdGpuPresent = isAmdGpuPresent ?? throw new ArgumentNullException(nameof(isAmdGpuPresent));
        _isRuntimePresent = isRuntimePresent ?? throw new ArgumentNullException(nameof(isRuntimePresent));
    }

    // The catalog would download the plugin on any linux-x64 host and then fail to load it, so
    // check what the plugin needs before touching the catalog.
    public Task<LinuxPluginCatalogResult> EnsureReadyAsync(
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        if (!_isAmdGpuPresent())
        {
            return Task.FromResult(new LinuxPluginCatalogResult(
                LinuxPluginCatalogStatus.Unsupported,
                "No AMD GPU detected on Linux."));
        }

        if (!_isRuntimePresent())
        {
            return Task.FromResult(new LinuxPluginCatalogResult(
                LinuxPluginCatalogStatus.Unsupported,
                $"The ROCm MIGraphX runtime ({MigraphxProviderConstants.LinuxRuntimeLibrary}) was not found. " +
                MigraphxProviderConstants.LinuxInstallHint));
        }

        return _plugin.EnsureReadyAsync(allowDownloads, cancellationToken);
    }
}
