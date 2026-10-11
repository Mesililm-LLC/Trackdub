namespace Trackdub.Contracts.ApplicationContracts;

public static class OpenVinoCatalogProviderIds
{
    public const string WinMl = "openvino-catalog-winml";

    /// <summary>Linux: the OpenVINO plugin EP pinned by the LinML catalog.</summary>
    public const string LinMl = "openvino-catalog-linml";
}

public static class QnnProviderIds
{
    public const string WinMl = "qnn-winml";
}

public static class VitisAiProviderIds
{
    public const string WinMl = "vitisai-winml";
}

public enum WinMlCatalogReadinessBlocker
{
    None = 0,
    HardwareNotSupported,
    GpuVendorMismatch,
    OsVersionUnsupported,
    DriverVersionMismatch,
    EpNotPresent,
    EpNotReady,
    EpPreparationPending,
    EpDownloadFailed,
    EpRegisterFailed,
    OrtProviderUnavailable,
    PlatformUnsupported,
    LicenseNotAcknowledged
}

public enum WinMlCatalogPlatformRoute
{
    None = 0,
    WinMlCatalog,

    /// <summary>Linux plugin EP installed and registered by the LinML catalog.</summary>
    LinMlCatalog
}

public sealed record WinMlCatalogReadinessReport(
    string ProviderId,
    WinMlCatalogPlatformRoute Route,
    WinMlCatalogReadinessBlocker Blocker,
    bool IsHardwareEligible,
    bool IsOrtProviderListed,
    bool IsRegisteredWithOrt,
    string Detail)
{
    public bool IsReady =>
        Blocker == WinMlCatalogReadinessBlocker.None &&
        IsHardwareEligible &&
        IsOrtProviderListed &&
        IsRegisteredWithOrt;
}

public interface IOpenVinoCatalogReadinessProbe
{
    Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public interface IQnnCatalogReadinessProbe
{
    Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public interface IVitisAiCatalogReadinessProbe
{
    Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public sealed record WinMlCatalogBootstrapResult(
    bool Succeeded,
    string? ProviderId,
    WinMlCatalogReadinessBlocker? Blocker,
    string Detail);
