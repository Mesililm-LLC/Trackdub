namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>Stable provider identifiers for manifest and readiness reporting.</summary>
public static class MigraphxProviderIds
{
    public const string WinMl = "migraphx-winml";
    public const string Rocm = "migraphx-rocm";
    public const string LinMl = "migraphx-linml";
}

public enum MigraphxReadinessBlocker
{
    None = 0,
    GpuVendorMismatch,
    OsVersionUnsupported,
    DriverVersionMismatch,
    EpNotPresent,
    EpPreparationPending,
    EpDownloadFailed,
    EpRegisterFailed,
    OrtProviderUnavailable,
    PlatformUnsupported
}

public enum MigraphxPlatformRoute
{
    None = 0,
    WinMlCatalog,
    NativeRocm,
    LinMlCatalog
}

public sealed record MigraphxReadinessReport(
    string ProviderId,
    MigraphxPlatformRoute Route,
    MigraphxReadinessBlocker Blocker,
    bool IsHardwareEligible,
    bool IsOrtProviderListed,
    bool IsRegisteredWithOrt,
    string Detail)
{
    public bool IsReady =>
        Blocker == MigraphxReadinessBlocker.None &&
        IsHardwareEligible &&
        IsOrtProviderListed &&
        IsRegisteredWithOrt;
}

public interface IMigraphxReadinessProbe
{
    Task<MigraphxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public interface IMigraphxProviderBootstrap
{
    Task<MigraphxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public sealed record MigraphxBootstrapResult(
    bool Succeeded,
    string? ProviderId,
    MigraphxReadinessBlocker? Blocker,
    string Detail);
