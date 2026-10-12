using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

public sealed class MigraphxReadinessProbe : IMigraphxReadinessProbe
{
#if WINDOWS
    private readonly WindowsMlMigraphxCatalogService _windowsCatalog = new();
#endif
    private readonly ILinuxNativeGpuRuntimeProbe _linuxRuntimeProbe = new LinuxNativeGpuRuntimeProbe();

    public async Task<MigraphxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default,
        IProgress<double>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return await ProbeWindowsAsync(allowProviderDownloads, cancellationToken, progress).ConfigureAwait(false);
        }
#endif

        if (OperatingSystem.IsLinux())
        {
            return ProbeLinux();
        }

        return new MigraphxReadinessReport(
            ProviderId: string.Empty,
            MigraphxPlatformRoute.None,
            MigraphxReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "MIGraphX is supported on Windows (WinML catalog) and Linux (system ROCm ORT build) only.");
    }

#if WINDOWS
    private async Task<MigraphxReadinessReport> ProbeWindowsAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken,
        IProgress<double>? progress)
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
            bootstrap = await _windowsCatalog.EnsureRegisteredAsync(true, cancellationToken, progress).ConfigureAwait(false);
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

    private MigraphxReadinessReport ProbeLinux()
    {
        const string providerId = MigraphxProviderIds.Rocm;
        bool hardwareEligible = _linuxRuntimeProbe.IsAmdGpuPresent();
        bool ortListed = MigraphxOrtProbe.IsProviderListed();

        if (!hardwareEligible)
        {
            return new MigraphxReadinessReport(
                providerId,
                MigraphxPlatformRoute.NativeRocm,
                MigraphxReadinessBlocker.GpuVendorMismatch,
                IsHardwareEligible: false,
                IsOrtProviderListed: ortListed,
                IsRegisteredWithOrt: false,
                Detail: "No AMD GPU detected on Linux.");
        }

        MigraphxReadinessBlocker blocker = ortListed
            ? MigraphxReadinessBlocker.None
            : MigraphxReadinessBlocker.OrtProviderUnavailable;

        return new MigraphxReadinessReport(
            providerId,
            MigraphxPlatformRoute.NativeRocm,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ortListed,
            IsRegisteredWithOrt: ortListed,
            Detail: ortListed
                ? "MIGraphXExecutionProvider is listed by ONNX Runtime."
                : MigraphxProviderConstants.LinuxInstallHint);
    }
}
