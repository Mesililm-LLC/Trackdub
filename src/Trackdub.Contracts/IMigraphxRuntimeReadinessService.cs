using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Contracts;

/// <summary>
/// Surfaces MIGraphX execution-provider readiness for Model Manager and settings UI.
/// Does not imply models are ready — only the platform EP path.
/// </summary>
public interface IMigraphxRuntimeReadinessService
{
    Task<MigraphxRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null);
}

public sealed record MigraphxRuntimeReadinessSnapshot(
    bool IsSupportedPlatform,
    bool IsReady,
    string? ProviderId,
    string RouteDisplay,
    string StatusLabel,
    MigraphxReadinessBlocker Blocker,
    string Detail,
    bool IsHardwareEligible,
    bool IsOrtProviderListed,
    bool IsRegisteredWithOrt,
    bool CanInstallWinMlProvider,
    string? InstallHint);
