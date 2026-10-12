using System.Runtime.Versioning;
using LinML.Catalog;
using LinML.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Onnx.ExecutionProviders.LinMl;

/// <summary>
/// Linux TensorRT-RTX bootstrap: NVIDIA's EP ABI plugin, vendored and registered through the LinML
/// catalog like the OpenVINO and MIGraphX plugins. A plugin directory the user chose (the studio
/// setting or <c>TRACKDUB_TRT_RTX_EP_DIR</c>) still wins and keeps its own registration path.
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed class LinMlTensorRtRtxBootstrap : ITensorRtRtxProviderBootstrap
{
    internal const string LicenseNotAcceptedDetail =
        "NVIDIA TensorRT RTX license not accepted. Accept the license in Model Manager before installing the plugin.";

    private readonly LinMlPluginCatalog _plugin;
    private readonly ITensorRtRtxProviderBootstrap _directoryBootstrap;
    private readonly Func<CancellationToken, ValueTask<string?>> _explicitPluginDirectory;
    private readonly Func<CancellationToken, ValueTask<bool>> _isLicenseAccepted;
    private readonly Func<(bool Eligible, TensorRtRtxReadinessBlocker Blocker, string Detail)> _hardwareGate;
    private readonly Func<string?, bool> _isOverrideUsable;
    private readonly LinMlWsl2TensorRtRtxPlugin _wsl2Plugin;
    private readonly Func<bool> _isPluginRegistered;

    public LinMlTensorRtRtxBootstrap(
        IExecutionProviderCatalog catalog,
        ITensorRtRtxProviderBootstrap directoryBootstrap,
        Func<CancellationToken, ValueTask<string?>> explicitPluginDirectory,
        Func<CancellationToken, ValueTask<bool>> isLicenseAccepted,
        Func<(bool Eligible, TensorRtRtxReadinessBlocker Blocker, string Detail)> hardwareGate,
        Func<string?, bool> isOverrideUsable,
        LinMlWsl2TensorRtRtxPlugin wsl2Plugin,
        Func<bool> isPluginRegistered)
    {
        _plugin = new LinMlPluginCatalog(
            catalog ?? throw new ArgumentNullException(nameof(catalog)),
            ProviderId.TensorrtRtx,
            "TensorRT-RTX");
        _directoryBootstrap = directoryBootstrap ?? throw new ArgumentNullException(nameof(directoryBootstrap));
        _explicitPluginDirectory = explicitPluginDirectory ?? throw new ArgumentNullException(nameof(explicitPluginDirectory));
        _isLicenseAccepted = isLicenseAccepted ?? throw new ArgumentNullException(nameof(isLicenseAccepted));
        _hardwareGate = hardwareGate ?? throw new ArgumentNullException(nameof(hardwareGate));
        _isOverrideUsable = isOverrideUsable ?? throw new ArgumentNullException(nameof(isOverrideUsable));
        _wsl2Plugin = wsl2Plugin ?? throw new ArgumentNullException(nameof(wsl2Plugin));
        _isPluginRegistered = isPluginRegistered ?? throw new ArgumentNullException(nameof(isPluginRegistered));
    }

    public static LinMlTensorRtRtxBootstrap Create(
        Func<CancellationToken, ValueTask<string?>> explicitPluginDirectory,
        Func<CancellationToken, ValueTask<bool>> isLicenseAccepted)
    {
        ArgumentNullException.ThrowIfNull(explicitPluginDirectory);

        return new LinMlTensorRtRtxBootstrap(
            ExecutionProviderCatalog.GetDefault(),
            new TensorRtRtxPluginService(explicitPluginDirectory, static _ => ValueTask.FromResult<string?>(null)),
            explicitPluginDirectory,
            isLicenseAccepted,
            LinuxNvidiaHardwareGate.Evaluate,
            static explicitDirectory => TensorRtRtxPluginLocator.Resolve(explicitDirectory).Succeeded,
            LinMlWsl2TensorRtRtxPlugin.CreateDefault(),
            TensorRtRtxOrtProbe.IsPluginProviderListed);
    }

    public async Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The catalog would download the plugin on any linux-x64 host and then expose no device, so
        // check for an NVIDIA driver before touching it.
        (bool eligible, TensorRtRtxReadinessBlocker hardwareBlocker, string hardwareDetail) = _hardwareGate();
        if (!eligible)
        {
            return Failure(hardwareBlocker, hardwareDetail);
        }

        // Another bootstrap in this process (for example the DI-wired one using the studio setting)
        // may already have registered the plugin; ORT keeps one registration per EP name.
        if (_isPluginRegistered())
        {
            return new TensorRtRtxBootstrapResult(
                Succeeded: true,
                TensorRtRtxProviderIds.PluginEpAbi,
                Blocker: null,
                "TensorRT RTX EP ABI plugin is already registered in this process.");
        }

        string? explicitDirectory = await _explicitPluginDirectory(cancellationToken).ConfigureAwait(false);
        if (_isOverrideUsable(explicitDirectory))
        {
            return await _directoryBootstrap
                .EnsureRegisteredAsync(allowProviderDownloads: false, cancellationToken)
                .ConfigureAwait(false);
        }

        // WSL2 needs LinML's patched plugin, registered before the catalog registers the stock one.
        LinMlWsl2PluginResult wsl2 = _wsl2Plugin.TryRegister();
        if (wsl2.Status is LinMlWsl2PluginStatus.Registered)
        {
            return new TensorRtRtxBootstrapResult(
                Succeeded: true,
                TensorRtRtxProviderIds.PluginEpAbi,
                Blocker: null,
                wsl2.Detail);
        }

        if (wsl2.Status is LinMlWsl2PluginStatus.Failed)
        {
            return Failure(TensorRtRtxReadinessBlocker.EpRegisterFailed, wsl2.Detail);
        }

        // The patched build links the stock plugin's libraries. With downloads allowed the catalog installs
        // them below (and registers the stock plugin, so the patched build takes over after a restart).
        if (wsl2.Status is LinMlWsl2PluginStatus.StockMissing && !allowProviderDownloads)
        {
            return Failure(TensorRtRtxReadinessBlocker.EpNotPresent, $"{wsl2.Detail} {wsl2.Hint}");
        }

        // The catalog downloads from PyPI without any license step, so the NVIDIA license gate
        // has to be applied here.
        bool allowDownloads = allowProviderDownloads
            && await _isLicenseAccepted(cancellationToken).ConfigureAwait(false);

        LinuxPluginCatalogResult catalog = await _plugin
            .EnsureReadyAsync(allowDownloads, cancellationToken)
            .ConfigureAwait(false);
        if (catalog.IsReady)
        {
            return new TensorRtRtxBootstrapResult(
                Succeeded: true,
                TensorRtRtxProviderIds.PluginEpAbi,
                Blocker: null,
                catalog.Detail);
        }

        if (allowProviderDownloads && !allowDownloads && catalog.Status is LinuxPluginCatalogStatus.NotInstalled)
        {
            return Failure(TensorRtRtxReadinessBlocker.EpDownloadFailed, LicenseNotAcceptedDetail);
        }

        string detail = catalog.Status is LinuxPluginCatalogStatus.NoDevices && wsl2.Hint is not null
            ? $"{catalog.Detail} {wsl2.Hint}"
            : catalog.Detail;
        return Failure(MapBlocker(catalog.Status, allowDownloads), detail);
    }

    internal static TensorRtRtxReadinessBlocker MapBlocker(LinuxPluginCatalogStatus status, bool downloadsAllowed) =>
        status switch
        {
            LinuxPluginCatalogStatus.NotInstalled => TensorRtRtxReadinessBlocker.EpNotPresent,
            LinuxPluginCatalogStatus.NoDevices => TensorRtRtxReadinessBlocker.OrtProviderUnavailable,
            LinuxPluginCatalogStatus.Unsupported => TensorRtRtxReadinessBlocker.PlatformUnsupported,
            _ => downloadsAllowed
                ? TensorRtRtxReadinessBlocker.EpDownloadFailed
                : TensorRtRtxReadinessBlocker.EpRegisterFailed,
        };

    private static TensorRtRtxBootstrapResult Failure(TensorRtRtxReadinessBlocker blocker, string detail) =>
        new(Succeeded: false, TensorRtRtxProviderIds.PluginEpAbi, blocker, detail);
}
