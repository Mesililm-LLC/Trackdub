using Trackdub.Application.Runtime;
using Trackdub.Domain;
#if !WINDOWS
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.WinMlCatalog;
#endif

namespace Trackdub.Composition.Runtime;

internal sealed class OpenVinoCatalogEpInstaller : IOpenVinoCatalogEpInstaller
{
#if WINDOWS
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return await WinMlCatalogEpInstallerCore.EnsureCoreAsync(
            ExecutionProviderKind.OpenVinoCatalog,
            "OpenVINO (WinML catalog)",
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#else
    private readonly IOpenVinoCatalogReadinessProbe _linuxProbe;

    public OpenVinoCatalogEpInstaller()
        : this(new OpenVinoCatalogReadinessProbe())
    {
    }

    internal OpenVinoCatalogEpInstaller(IOpenVinoCatalogReadinessProbe linuxProbe)
    {
        _linuxProbe = linuxProbe;
    }

    // Linux: the LinML catalog downloads, verifies and registers the pinned OpenVINO plugin EP.
    // License consent is obtained by the caller before installing, as on Windows.
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!OperatingSystem.IsLinux())
        {
            return await WinMlCatalogEpInstallerCore.Unsupported(progress, cancellationToken).ConfigureAwait(false);
        }

        progress.Report("Downloading and registering the OpenVINO plugin (LinML catalog)…");
        WinMlCatalogReadinessReport report = await _linuxProbe
            .ProbeAsync(allowProviderDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        progress.Report(report.Detail);
        return report.IsReady
            ? new WinMlCatalogEpInstallResult(Succeeded: true)
            : new WinMlCatalogEpInstallResult(Succeeded: false, FailureDetail: report.Detail);
    }
#endif
}

internal sealed class QnnCatalogEpInstaller : IQnnCatalogEpInstaller
{
#if WINDOWS
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return await WinMlCatalogEpInstallerCore.EnsureCoreAsync(
            ExecutionProviderKind.Qnn,
            "QNN (WinML catalog)",
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#else
    public Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        WinMlCatalogEpInstallerCore.Unsupported(progress, cancellationToken);
#endif
}

internal sealed class VitisAiCatalogEpInstaller : IVitisAiCatalogEpInstaller
{
#if WINDOWS
    public async Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return await WinMlCatalogEpInstallerCore.EnsureCoreAsync(
            ExecutionProviderKind.VitisAi,
            "Vitis AI (WinML catalog)",
            progress,
            cancellationToken).ConfigureAwait(false);
    }
#else
    public Task<WinMlCatalogEpInstallResult> EnsureInstalledAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default) =>
        WinMlCatalogEpInstallerCore.Unsupported(progress, cancellationToken);
#endif
}
