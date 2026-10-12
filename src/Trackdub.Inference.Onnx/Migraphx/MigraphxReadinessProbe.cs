using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.ExecutionProviders.LinMl;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

public sealed class MigraphxReadinessProbe : IMigraphxReadinessProbe
{
#if WINDOWS
    private readonly WindowsMlMigraphxCatalogService _windowsCatalog = new();
#endif
    private readonly ILinuxNativeGpuRuntimeProbe _linuxRuntimeProbe;
    private readonly ILinuxMigraphxCatalog _linuxCatalog;

    public MigraphxReadinessProbe()
        : this(new LinuxNativeGpuRuntimeProbe(), DefaultLinuxCatalog())
    {
    }

    internal MigraphxReadinessProbe(ILinuxNativeGpuRuntimeProbe linuxRuntimeProbe, ILinuxMigraphxCatalog linuxCatalog)
    {
        _linuxRuntimeProbe = linuxRuntimeProbe ?? throw new ArgumentNullException(nameof(linuxRuntimeProbe));
        _linuxCatalog = linuxCatalog ?? throw new ArgumentNullException(nameof(linuxCatalog));
    }

    private static ILinuxMigraphxCatalog DefaultLinuxCatalog()
    {
#if LINML
        if (OperatingSystem.IsLinux())
        {
            return LinMlMigraphxCatalog.Default;
        }
#endif
        return UnavailableLinuxMigraphxCatalog.Instance;
    }

    public async Task<MigraphxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return await ProbeWindowsAsync(allowProviderDownloads, cancellationToken).ConfigureAwait(false);
        }
#endif

        if (OperatingSystem.IsLinux())
        {
            return await ProbeLinuxAsync(allowProviderDownloads, cancellationToken).ConfigureAwait(false);
        }

        return new MigraphxReadinessReport(
            ProviderId: string.Empty,
            MigraphxPlatformRoute.None,
            MigraphxReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "MIGraphX is supported on Windows (WinML catalog) and Linux (LinML catalog plugin EP) only.");
    }

#if WINDOWS
    private async Task<MigraphxReadinessReport> ProbeWindowsAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        const string providerId = MigraphxProviderIds.WinMl;
        (bool eligible, MigraphxReadinessBlocker hardwareBlocker, string hardwareDetail) =
            WindowsMigraphxHardwareGate.Evaluate();

        bool ortListed = MigraphxOrtProbe.IsProviderListed();
        if (!eligible)
        {
            return new MigraphxReadinessReport(
                providerId,
                MigraphxPlatformRoute.WinMlCatalog,
                hardwareBlocker,
                IsHardwareEligible: false,
                IsOrtProviderListed: ortListed,
                IsRegisteredWithOrt: ortListed,
                Detail: hardwareDetail);
        }

        MigraphxBootstrapResult bootstrap = await _windowsCatalog
            .EnsureRegisteredAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);

        if (!bootstrap.Succeeded && bootstrap.Blocker is MigraphxReadinessBlocker.EpNotPresent && allowProviderDownloads)
        {
            bootstrap = await _windowsCatalog.EnsureRegisteredAsync(true, cancellationToken).ConfigureAwait(false);
        }

        ortListed = MigraphxOrtProbe.IsProviderListed();
        MigraphxReadinessBlocker blocker = bootstrap.Succeeded
            ? MigraphxReadinessBlocker.None
            : bootstrap.Blocker ?? MigraphxReadinessBlocker.EpRegisterFailed;

        return new MigraphxReadinessReport(
            providerId,
            MigraphxPlatformRoute.WinMlCatalog,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ortListed,
            IsRegisteredWithOrt: ortListed && bootstrap.Succeeded,
            Detail: bootstrap.Detail);
    }
#endif

    private async Task<MigraphxReadinessReport> ProbeLinuxAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        if (!_linuxRuntimeProbe.IsAmdGpuPresent())
        {
            return new MigraphxReadinessReport(
                MigraphxProviderIds.LinMl,
                MigraphxPlatformRoute.LinMlCatalog,
                MigraphxReadinessBlocker.GpuVendorMismatch,
                IsHardwareEligible: false,
                IsOrtProviderListed: false,
                IsRegisteredWithOrt: false,
                Detail: "No AMD GPU detected on Linux.");
        }

        // An ONNX Runtime build with MIGraphX compiled in needs no plugin.
        if (_linuxRuntimeProbe.IsMigraphxOrtProviderAvailable())
        {
            return new MigraphxReadinessReport(
                MigraphxProviderIds.Rocm,
                MigraphxPlatformRoute.NativeRocm,
                MigraphxReadinessBlocker.None,
                IsHardwareEligible: true,
                IsOrtProviderListed: true,
                IsRegisteredWithOrt: true,
                Detail: "MIGraphXExecutionProvider is listed by ONNX Runtime.");
        }

        LinuxPluginCatalogResult catalog = await _linuxCatalog
            .EnsureReadyAsync(allowProviderDownloads, cancellationToken)
            .ConfigureAwait(false);

        MigraphxReadinessBlocker blocker = catalog.Status switch
        {
            LinuxPluginCatalogStatus.Ready => MigraphxReadinessBlocker.None,
            LinuxPluginCatalogStatus.NotInstalled => MigraphxReadinessBlocker.EpNotPresent,
            LinuxPluginCatalogStatus.Failed when allowProviderDownloads => MigraphxReadinessBlocker.EpDownloadFailed,
            LinuxPluginCatalogStatus.Failed or LinuxPluginCatalogStatus.NoDevices => MigraphxReadinessBlocker.EpRegisterFailed,
            _ => MigraphxReadinessBlocker.OrtProviderUnavailable,
        };

        return new MigraphxReadinessReport(
            MigraphxProviderIds.LinMl,
            MigraphxPlatformRoute.LinMlCatalog,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: catalog.IsReady,
            IsRegisteredWithOrt: catalog.IsReady,
            Detail: catalog.Detail);
    }
}
