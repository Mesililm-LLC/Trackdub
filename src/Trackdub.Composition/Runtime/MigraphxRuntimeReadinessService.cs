using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Composition.Runtime;

public sealed class MigraphxRuntimeReadinessService(IMigraphxReadinessProbe readinessProbe)
    : IMigraphxRuntimeReadinessService
{
    private readonly IMigraphxReadinessProbe readinessProbe =
        readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));

    public async Task<MigraphxRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        MigraphxReadinessReport report = await readinessProbe
            .ProbeAsync(allowProviderDownloads, cancellationToken, progress)
            .ConfigureAwait(false);

        if (report.Route is MigraphxPlatformRoute.None)
        {
            return new MigraphxRuntimeReadinessSnapshot(
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
            return new MigraphxRuntimeReadinessSnapshot(
                IsSupportedPlatform: true,
                IsReady: true,
                ProviderId: report.ProviderId,
                RouteDisplay: FormatRoute(report.Route),
                StatusLabel: "Ready",
                Blocker: MigraphxReadinessBlocker.None,
                Detail: report.Detail,
                IsHardwareEligible: report.IsHardwareEligible,
                IsOrtProviderListed: report.IsOrtProviderListed,
                IsRegisteredWithOrt: report.IsRegisteredWithOrt,
                CanInstallWinMlProvider: false,
                InstallHint: null);
        }

        bool canInstall = report.Route is MigraphxPlatformRoute.WinMlCatalog or MigraphxPlatformRoute.LinMlCatalog &&
                          report.Blocker is MigraphxReadinessBlocker.EpNotPresent or MigraphxReadinessBlocker.EpDownloadFailed;

        string? installHint = report.Route switch
        {
            MigraphxPlatformRoute.WinMlCatalog when canInstall =>
                "Download the Windows ML MIGraphX execution provider package, then refresh.",
            MigraphxPlatformRoute.LinMlCatalog when canInstall =>
                "Download AMD's MIGraphX plugin execution provider through the LinML catalog, then refresh.",
            MigraphxPlatformRoute.NativeRocm or MigraphxPlatformRoute.LinMlCatalog =>
                MigraphxProviderConstants.LinuxInstallHint,
            _ => null
        };

        return new MigraphxRuntimeReadinessSnapshot(
            IsSupportedPlatform: true,
            IsReady: false,
            ProviderId: string.IsNullOrWhiteSpace(report.ProviderId) ? null : report.ProviderId,
            RouteDisplay: FormatRoute(report.Route),
            StatusLabel: FormatBlockedStatus(report.Blocker),
            Blocker: report.Blocker,
            Detail: report.Detail,
            IsHardwareEligible: report.IsHardwareEligible,
            IsOrtProviderListed: report.IsOrtProviderListed,
            IsRegisteredWithOrt: report.IsRegisteredWithOrt,
            CanInstallWinMlProvider: canInstall,
            InstallHint: installHint);
    }

    private static string FormatRoute(MigraphxPlatformRoute route) =>
        route switch
        {
            MigraphxPlatformRoute.WinMlCatalog => "Windows (WinML catalog)",
            MigraphxPlatformRoute.NativeRocm => "Linux (system ROCm ORT)",
            MigraphxPlatformRoute.LinMlCatalog => "Linux (LinML catalog)",
            _ => "Not supported"
        };

    private static string FormatBlockedStatus(MigraphxReadinessBlocker blocker) =>
        blocker switch
        {
            MigraphxReadinessBlocker.None => "Not ready",
            MigraphxReadinessBlocker.EpNotPresent or MigraphxReadinessBlocker.EpDownloadFailed => "Install required",
            MigraphxReadinessBlocker.EpPreparationPending => "Preparing",
            MigraphxReadinessBlocker.DriverVersionMismatch or MigraphxReadinessBlocker.OsVersionUnsupported => "Blocked",
            MigraphxReadinessBlocker.GpuVendorMismatch => "No AMD GPU",
            MigraphxReadinessBlocker.OrtProviderUnavailable => "ORT EP missing",
            MigraphxReadinessBlocker.EpRegisterFailed => "Registration failed",
            _ => "Blocked"
        };
}
