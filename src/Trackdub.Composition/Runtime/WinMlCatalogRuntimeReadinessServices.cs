using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Runtime;

public sealed class OpenVinoCatalogRuntimeReadinessService(IOpenVinoCatalogReadinessProbe readinessProbe)
    : WinMlCatalogRuntimeReadinessServiceBase(readinessProbe), IOpenVinoCatalogRuntimeReadinessService
{
    protected override string RouteLabel => "Windows (WinML catalog)";

    protected override string InstallHint =>
        "Download the Windows ML OpenVINO execution provider package, then refresh.";
}

public sealed class QnnCatalogRuntimeReadinessService(IQnnCatalogReadinessProbe readinessProbe)
    : WinMlCatalogRuntimeReadinessServiceBase(readinessProbe), IQnnCatalogRuntimeReadinessService
{
    protected override string RouteLabel => "Windows (WinML catalog)";

    protected override string InstallHint =>
        "Download the Windows ML QNN execution provider package, then refresh.";
}

public sealed class VitisAiCatalogRuntimeReadinessService(IVitisAiCatalogReadinessProbe readinessProbe)
    : WinMlCatalogRuntimeReadinessServiceBase(readinessProbe), IVitisAiCatalogRuntimeReadinessService
{
    protected override string RouteLabel => "Windows (WinML catalog)";

    protected override string InstallHint =>
        "Download the Windows ML Vitis AI execution provider package, then refresh.";
}

public abstract class WinMlCatalogRuntimeReadinessServiceBase
{
    private readonly Func<bool, CancellationToken, Task<WinMlCatalogReadinessReport>> _probeAsync;

    protected WinMlCatalogRuntimeReadinessServiceBase(IOpenVinoCatalogReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probeAsync = probe.ProbeAsync;
    }

    protected WinMlCatalogRuntimeReadinessServiceBase(IQnnCatalogReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probeAsync = probe.ProbeAsync;
    }

    protected WinMlCatalogRuntimeReadinessServiceBase(IVitisAiCatalogReadinessProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probeAsync = probe.ProbeAsync;
    }

    protected abstract string RouteLabel { get; }

    protected abstract string InstallHint { get; }

    protected virtual string LinuxRouteLabel => "Linux (LinML catalog)";

    protected virtual string LinuxInstallHint =>
        "Download the pinned execution provider plugin through the LinML catalog, then refresh.";

    public async Task<WinMlCatalogRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        WinMlCatalogReadinessReport report = await _probeAsync(allowProviderDownloads, cancellationToken)
            .ConfigureAwait(false);
        bool linux = report.Route is WinMlCatalogPlatformRoute.LinMlCatalog;
        string routeLabel = linux ? LinuxRouteLabel : RouteLabel;
        string installHint = linux ? LinuxInstallHint : InstallHint;

        if (report.Route is WinMlCatalogPlatformRoute.None)
        {
            return new WinMlCatalogRuntimeReadinessSnapshot(
                IsSupportedPlatform: false,
                IsReady: false,
                ProviderId: null,
                RouteDisplay: "Not supported",
                StatusLabel: "Unavailable",
                Blocker: report.Blocker,
                Detail: report.Detail,
                IsHardwareEligible: report.IsHardwareEligible,
                IsOrtProviderListed: report.IsOrtProviderListed,
                IsRegisteredWithOrt: report.IsRegisteredWithOrt,
                CanInstallWinMlProvider: false,
                InstallHint: null);
        }

        if (report.IsReady)
        {
            return new WinMlCatalogRuntimeReadinessSnapshot(
                IsSupportedPlatform: true,
                IsReady: true,
                ProviderId: report.ProviderId,
                RouteDisplay: routeLabel,
                StatusLabel: "Ready",
                Blocker: WinMlCatalogReadinessBlocker.None,
                Detail: report.Detail,
                IsHardwareEligible: report.IsHardwareEligible,
                IsOrtProviderListed: report.IsOrtProviderListed,
                IsRegisteredWithOrt: report.IsRegisteredWithOrt,
                CanInstallWinMlProvider: false,
                InstallHint: null);
        }

        bool canInstall = report.Route is (WinMlCatalogPlatformRoute.WinMlCatalog or WinMlCatalogPlatformRoute.LinMlCatalog) &&
                          report.Blocker is WinMlCatalogReadinessBlocker.EpNotPresent
                              or WinMlCatalogReadinessBlocker.EpDownloadFailed;

        return new WinMlCatalogRuntimeReadinessSnapshot(
            IsSupportedPlatform: true,
            IsReady: false,
            ProviderId: string.IsNullOrWhiteSpace(report.ProviderId) ? null : report.ProviderId,
            RouteDisplay: routeLabel,
            StatusLabel: FormatBlockedStatus(report.Blocker),
            Blocker: report.Blocker,
            Detail: report.Detail,
            IsHardwareEligible: report.IsHardwareEligible,
            IsOrtProviderListed: report.IsOrtProviderListed,
            IsRegisteredWithOrt: report.IsRegisteredWithOrt,
            CanInstallWinMlProvider: canInstall,
            InstallHint: canInstall ? installHint : null);
    }

    private static string FormatBlockedStatus(WinMlCatalogReadinessBlocker blocker) =>
        blocker switch
        {
            WinMlCatalogReadinessBlocker.None => "Not ready",
            WinMlCatalogReadinessBlocker.EpNotPresent or WinMlCatalogReadinessBlocker.EpDownloadFailed =>
                "Install required",
            WinMlCatalogReadinessBlocker.EpNotReady => "Prepare installed provider",
            WinMlCatalogReadinessBlocker.EpPreparationPending => "Preparing",
            WinMlCatalogReadinessBlocker.HardwareNotSupported or WinMlCatalogReadinessBlocker.GpuVendorMismatch =>
                "Hardware mismatch",
            WinMlCatalogReadinessBlocker.DriverVersionMismatch or WinMlCatalogReadinessBlocker.OsVersionUnsupported =>
                "Blocked",
            WinMlCatalogReadinessBlocker.OrtProviderUnavailable => "ORT EP missing",
            WinMlCatalogReadinessBlocker.EpRegisterFailed => "Registration failed",
            WinMlCatalogReadinessBlocker.LicenseNotAcknowledged => "License required",
            _ => "Blocked"
        };
}
