using Trackdub.Application.Runtime;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.Runtime;

public sealed class GpuRuntimeInstallOrchestrator(
    ITrtRtxEpInstaller? trtRtxEpInstaller = null,
    IMigraphxRuntimeReadinessService? migraphxReadiness = null,
    IOpenVinoCatalogEpInstaller? openVinoInstaller = null,
    IQnnCatalogEpInstaller? qnnInstaller = null,
    IVitisAiCatalogEpInstaller? vitisAiInstaller = null,
    IWindowsMlCertifiedCatalogInstaller? certifiedCatalogInstaller = null,
    ITensorRtRtxRuntimeReadinessService? tensorRtRtxReadiness = null,
    ITensorRtRtxReadinessProbe? tensorRtRtxReadinessProbe = null,
    IOpenVinoCatalogReadinessProbe? openVinoReadinessProbe = null,
    IQnnCatalogReadinessProbe? qnnReadinessProbe = null,
    IVitisAiCatalogReadinessProbe? vitisAiReadinessProbe = null) : IGpuRuntimeInstallOrchestrator
{
    // Invalidates the shared (process-wide singleton) readiness cache for a probe after a
    // state-changing install so a later status/list/verification re-probe observes the freshly
    // installed state instead of a stale pre-install snapshot. This forces a real re-probe via the
    // IReadinessProbeCache soft cast; it never fabricates a ready result. A non-caching probe (or a
    // null handle in a reduced build) is simply skipped.
    private static void InvalidateReadinessCache(object? probe)
    {
        if (probe is IReadinessProbeCache cache)
        {
            cache.Invalidate();
        }
    }

    public async Task<GpuRuntimeInstallResult> InstallAsync(
        StarterPackGpuRuntimeKind runtimeKind,
        IProgress<string> progress,
        CancellationToken cancellationToken = default,
        IProgress<double>? percentProgress = null)
    {
        ArgumentNullException.ThrowIfNull(progress);

        // Linux installs only the OpenVINO plugin EP, through the LinML catalog.
        if (!OperatingSystem.IsWindows()
            && !(OperatingSystem.IsLinux() && runtimeKind is StarterPackGpuRuntimeKind.IntelOpenVino))
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "GPU runtime install is only available on Windows (and Intel OpenVINO on Linux).");
        }

        return runtimeKind switch
        {
            StarterPackGpuRuntimeKind.NvidiaTensorRtRtx => await InstallTensorRtRtxAsync(progress, cancellationToken)
                .ConfigureAwait(false),
            StarterPackGpuRuntimeKind.AmdMigraphx => await InstallMigraphxAsync(progress, percentProgress, cancellationToken)
                .ConfigureAwait(false),
            StarterPackGpuRuntimeKind.IntelOpenVino => await InstallOpenVinoAsync(progress, cancellationToken)
                .ConfigureAwait(false),
            StarterPackGpuRuntimeKind.QualcommQnn => await InstallQnnAsync(progress, cancellationToken)
                .ConfigureAwait(false),
            StarterPackGpuRuntimeKind.AmdVitisAi => await InstallVitisAiAsync(progress, cancellationToken)
                .ConfigureAwait(false),
            StarterPackGpuRuntimeKind.WindowsMlCatalogBundle => await InstallCertifiedCatalogBundleAsync(
                progress,
                cancellationToken).ConfigureAwait(false),
            _ => new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "No GPU runtime install action is available for this machine."),
        };
    }

    private async Task<GpuRuntimeInstallResult> InstallTensorRtRtxAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (trtRtxEpInstaller is null)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "TensorRT RTX installer is unavailable in this build.");
        }

        progress.Report("Installing TensorRT RTX GPU runtime…");
        TrtRtxEpInstallResult result = await trtRtxEpInstaller
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            InvalidateReadinessCache(tensorRtRtxReadinessProbe);
            return new GpuRuntimeInstallResult(Succeeded: true, Detail: "TensorRT RTX GPU runtime is installed.");
        }

        return new GpuRuntimeInstallResult(
            Succeeded: false,
            FailureDetail: result.FailureDetail ?? "TensorRT RTX install failed.");
    }

    private async Task<GpuRuntimeInstallResult> InstallMigraphxAsync(
        IProgress<string> progress,
        IProgress<double>? percentProgress,
        CancellationToken cancellationToken)
    {
        if (migraphxReadiness is null)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "AMD MIGraphX readiness service is unavailable in this build.");
        }

        progress.Report("Installing AMD MIGraphX GPU runtime…");
        MigraphxRuntimeReadinessSnapshot snapshot = await migraphxReadiness
            .ProbeAsync(allowProviderDownloads: true, cancellationToken, percentProgress)
            .ConfigureAwait(false);

        if (snapshot.IsReady)
        {
            return new GpuRuntimeInstallResult(Succeeded: true, Detail: "AMD MIGraphX GPU runtime is ready.");
        }

        return new GpuRuntimeInstallResult(
            Succeeded: false,
            FailureDetail: snapshot.Detail);
    }

    private async Task<GpuRuntimeInstallResult> InstallOpenVinoAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (openVinoInstaller is null)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "Intel OpenVINO installer is unavailable in this build.");
        }

        progress.Report("Installing Intel OpenVINO GPU runtime…");
        WinMlCatalogEpInstallResult result = await openVinoInstaller
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            InvalidateReadinessCache(openVinoReadinessProbe);
            return new GpuRuntimeInstallResult(Succeeded: true, Detail: "Intel OpenVINO GPU runtime is installed.");
        }

        return new GpuRuntimeInstallResult(
            Succeeded: false,
            FailureDetail: result.FailureDetail ?? "Intel OpenVINO install failed.");
    }

    private async Task<GpuRuntimeInstallResult> InstallQnnAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (qnnInstaller is null)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "Qualcomm QNN installer is unavailable in this build.");
        }

        progress.Report("Installing Qualcomm QNN GPU runtime…");
        WinMlCatalogEpInstallResult result = await qnnInstaller
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            InvalidateReadinessCache(qnnReadinessProbe);
            return new GpuRuntimeInstallResult(Succeeded: true, Detail: "Qualcomm QNN GPU runtime is installed.");
        }

        return new GpuRuntimeInstallResult(
            Succeeded: false,
            FailureDetail: result.FailureDetail ?? "Qualcomm QNN install failed.");
    }

    private async Task<GpuRuntimeInstallResult> InstallVitisAiAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (vitisAiInstaller is null)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "AMD VitisAI installer is unavailable in this build.");
        }

        progress.Report("Installing AMD VitisAI NPU runtime…");
        WinMlCatalogEpInstallResult result = await vitisAiInstaller
            .EnsureInstalledAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (result.Succeeded)
        {
            InvalidateReadinessCache(vitisAiReadinessProbe);
            return new GpuRuntimeInstallResult(Succeeded: true, Detail: "AMD VitisAI NPU runtime is installed.");
        }

        return new GpuRuntimeInstallResult(
            Succeeded: false,
            FailureDetail: result.FailureDetail ?? "AMD VitisAI install failed.");
    }

    private async Task<GpuRuntimeInstallResult> InstallCertifiedCatalogBundleAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        if (certifiedCatalogInstaller is null)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: "Windows ML catalog installer is unavailable in this build.");
        }

        progress.Report("Installing Windows ML certified GPU runtimes…");
        WindowsMlCertifiedCatalogInstallResult catalogResult = await certifiedCatalogInstaller
            .EnsureAllCertifiedAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        if (!catalogResult.Succeeded)
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                FailureDetail: catalogResult.FailureDetail ?? catalogResult.Detail);
        }

        // The certified-catalog install registers all certified catalog providers (OpenVINO, QNN,
        // and VitisAI), so invalidate those caches before any subsequent probes to ensure later
        // status/list operations observe the freshly registered state.
        InvalidateReadinessCache(openVinoReadinessProbe);
        InvalidateReadinessCache(qnnReadinessProbe);
        InvalidateReadinessCache(vitisAiReadinessProbe);

        GpuRuntimeInstallResult? trtResult = null;
        if (tensorRtRtxReadiness is not null && trtRtxEpInstaller is not null)
        {
            // The certified-catalog install may register execution providers that affect TRT RTX
            // readiness. Invalidate the shared TRT RTX readiness cache before the gating probe so
            // the install-or-skip decision below is made against fresh post-catalog-install state
            // rather than a stale pre-install cached snapshot from earlier in the process.
            InvalidateReadinessCache(tensorRtRtxReadinessProbe);

            TensorRtRtxRuntimeReadinessSnapshot trtSnapshot = await tensorRtRtxReadiness
                .ProbeAsync(allowProviderDownloads: false, cancellationToken)
                .ConfigureAwait(false);
            if (trtSnapshot.IsHardwareEligible && trtSnapshot.CanInstallWinMlProvider && !trtSnapshot.IsReady)
            {
                trtResult = await InstallTensorRtRtxAsync(progress, cancellationToken).ConfigureAwait(false);
            }
        }

        if (trtResult is { Succeeded: false })
        {
            return new GpuRuntimeInstallResult(
                Succeeded: false,
                Detail: catalogResult.Detail,
                FailureDetail: trtResult.FailureDetail);
        }

        string detail = trtResult?.Detail is null
            ? catalogResult.Detail
            : $"{catalogResult.Detail} {trtResult.Detail}";
        return new GpuRuntimeInstallResult(Succeeded: true, Detail: detail);
    }
}
