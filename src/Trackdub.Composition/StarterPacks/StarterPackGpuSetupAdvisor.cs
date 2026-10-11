using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackGpuSetupAdvisor(
    IHardwareProfileProvider hardwareProfileProvider,
    ITensorRtRtxRuntimeReadinessService? tensorRtRtxReadiness = null,
    IMigraphxRuntimeReadinessService? migraphxReadiness = null,
    IOpenVinoCatalogRuntimeReadinessService? openVinoReadiness = null,
    IQnnCatalogRuntimeReadinessService? qnnReadiness = null,
    IVitisAiCatalogRuntimeReadinessService? vitisAiReadiness = null)
{
    public async Task<StarterPackGpuSetupHint?> ResolveAsync(
        StarterPackCompatibilityReport? compatibilityReport,
        CancellationToken cancellationToken = default)
    {
        if (compatibilityReport is null || !compatibilityReport.AnyFallbackApplied)
        {
            return null;
        }

        IReadOnlyList<StageCompatibilityEntry> gpuEpFallbackStages = compatibilityReport.Stages
            .Where(IsGpuExecutionProviderFallback)
            .ToList();

        bool requiresVariantOptimization = compatibilityReport.Stages.Any(stage =>
            stage.FallbackApplied &&
            string.Equals(stage.FallbackReason, "variant_not_installed", StringComparison.OrdinalIgnoreCase));

        if (gpuEpFallbackStages.Count == 0)
        {
            return requiresVariantOptimization
                ? new StarterPackGpuSetupHint(
                    StarterPackGpuRuntimeKind.None,
                    CanInstall: false,
                    RequiresGpuVariantOptimization: true,
                    GpuFallbackStageCount: 0)
                : null;
        }

        HardwareProfile hardware = await hardwareProfileProvider
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        StarterPackGpuRuntimeKind runtimeKind = ResolveRecommendedRuntimeKind(hardware, gpuEpFallbackStages);
        bool isWindows = hardware.OperatingSystem.Equals("windows", StringComparison.OrdinalIgnoreCase);
        // Linux can install only the OpenVINO plugin EP (LinML catalog).
        bool isLinuxOpenVino = hardware.OperatingSystem.Equals("linux", StringComparison.OrdinalIgnoreCase)
            && runtimeKind is StarterPackGpuRuntimeKind.IntelOpenVino;
        if (!isWindows && !isLinuxOpenVino)
        {
            return new StarterPackGpuSetupHint(
                StarterPackGpuRuntimeKind.None,
                CanInstall: false,
                RequiresGpuVariantOptimization: requiresVariantOptimization,
                GpuFallbackStageCount: gpuEpFallbackStages.Count);
        }

        bool canInstall = await CanInstallRuntimeKindAsync(runtimeKind, cancellationToken).ConfigureAwait(false);

        return new StarterPackGpuSetupHint(
            runtimeKind,
            canInstall,
            requiresVariantOptimization,
            gpuEpFallbackStages.Count);
    }

    internal static StarterPackGpuRuntimeKind ResolveRecommendedRuntimeKind(
        HardwareProfile hardware,
        IReadOnlyList<StageCompatibilityEntry> gpuEpFallbackStages)
    {
        HashSet<string> requestedProviders = gpuEpFallbackStages
            .Select(stage => NormalizeExecutionProviderToken(stage.RequestedExecutionProvider))
            .Where(token => token is not "cpu" and not "auto")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        GpuHardwareVendor vendor = DetectGpuVendor(hardware);

        if (requestedProviders.Contains("trt-rtx") || requestedProviders.Contains("tensorrt-rtx"))
        {
            return StarterPackGpuRuntimeKind.NvidiaTensorRtRtx;
        }

        if (requestedProviders.Contains("cuda"))
        {
            return vendor switch
            {
                GpuHardwareVendor.Nvidia => StarterPackGpuRuntimeKind.NvidiaTensorRtRtx,
                _ => StarterPackGpuRuntimeKind.WindowsMlCatalogBundle,
            };
        }

        if (requestedProviders.Contains("migraphx"))
        {
            return StarterPackGpuRuntimeKind.AmdMigraphx;
        }

        return vendor switch
        {
            GpuHardwareVendor.Nvidia => StarterPackGpuRuntimeKind.NvidiaTensorRtRtx,
            GpuHardwareVendor.Amd => StarterPackGpuRuntimeKind.AmdMigraphx,
            GpuHardwareVendor.Intel => StarterPackGpuRuntimeKind.IntelOpenVino,
            GpuHardwareVendor.Qualcomm => StarterPackGpuRuntimeKind.QualcommQnn,
            _ => StarterPackGpuRuntimeKind.WindowsMlCatalogBundle,
        };
    }

    private async Task<bool> CanInstallRuntimeKindAsync(
        StarterPackGpuRuntimeKind runtimeKind,
        CancellationToken cancellationToken)
    {
        return runtimeKind switch
        {
            StarterPackGpuRuntimeKind.NvidiaTensorRtRtx =>
                await CanInstallTensorRtRtxAsync(cancellationToken).ConfigureAwait(false),
            StarterPackGpuRuntimeKind.AmdMigraphx =>
                await CanInstallMigraphxAsync(cancellationToken).ConfigureAwait(false),
            StarterPackGpuRuntimeKind.IntelOpenVino =>
                await CanInstallCatalogAsync(openVinoReadiness, cancellationToken).ConfigureAwait(false),
            StarterPackGpuRuntimeKind.QualcommQnn =>
                await CanInstallCatalogAsync(qnnReadiness, cancellationToken).ConfigureAwait(false),
            StarterPackGpuRuntimeKind.AmdVitisAi =>
                await CanInstallCatalogAsync(vitisAiReadiness, cancellationToken).ConfigureAwait(false),
            StarterPackGpuRuntimeKind.WindowsMlCatalogBundle =>
                await CanInstallTensorRtRtxAsync(cancellationToken).ConfigureAwait(false) ||
                await CanInstallMigraphxAsync(cancellationToken).ConfigureAwait(false) ||
                await CanInstallCatalogAsync(openVinoReadiness, cancellationToken).ConfigureAwait(false) ||
                await CanInstallCatalogAsync(qnnReadiness, cancellationToken).ConfigureAwait(false) ||
                await CanInstallCatalogAsync(vitisAiReadiness, cancellationToken).ConfigureAwait(false),
            _ => false,
        };
    }

    private async Task<bool> CanInstallTensorRtRtxAsync(CancellationToken cancellationToken)
    {
        if (tensorRtRtxReadiness is null)
        {
            return false;
        }

        TensorRtRtxRuntimeReadinessSnapshot snapshot = await tensorRtRtxReadiness
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.IsSupportedPlatform &&
               snapshot.IsHardwareEligible &&
               snapshot.CanInstallWinMlProvider;
    }

    private async Task<bool> CanInstallMigraphxAsync(CancellationToken cancellationToken)
    {
        if (migraphxReadiness is null)
        {
            return false;
        }

        MigraphxRuntimeReadinessSnapshot snapshot = await migraphxReadiness
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.IsSupportedPlatform &&
               snapshot.IsHardwareEligible &&
               snapshot.CanInstallWinMlProvider;
    }

    private static async Task<bool> CanInstallCatalogAsync(
        IOpenVinoCatalogRuntimeReadinessService? readiness,
        CancellationToken cancellationToken)
    {
        if (readiness is null)
        {
            return false;
        }

        WinMlCatalogRuntimeReadinessSnapshot snapshot = await readiness
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.IsSupportedPlatform &&
               snapshot.IsHardwareEligible &&
               snapshot.CanInstallWinMlProvider;
    }

    private static async Task<bool> CanInstallCatalogAsync(
        IQnnCatalogRuntimeReadinessService? readiness,
        CancellationToken cancellationToken)
    {
        if (readiness is null)
        {
            return false;
        }

        WinMlCatalogRuntimeReadinessSnapshot snapshot = await readiness
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.IsSupportedPlatform &&
               snapshot.IsHardwareEligible &&
               snapshot.CanInstallWinMlProvider;
    }

    private static async Task<bool> CanInstallCatalogAsync(
        IVitisAiCatalogRuntimeReadinessService? readiness,
        CancellationToken cancellationToken)
    {
        if (readiness is null)
        {
            return false;
        }

        WinMlCatalogRuntimeReadinessSnapshot snapshot = await readiness
            .ProbeAsync(allowProviderDownloads: false, cancellationToken)
            .ConfigureAwait(false);
        return snapshot.IsSupportedPlatform &&
               snapshot.IsHardwareEligible &&
               snapshot.CanInstallWinMlProvider;
    }

    private static bool IsGpuExecutionProviderFallback(StageCompatibilityEntry stage)
    {
        if (!stage.FallbackApplied)
        {
            return false;
        }

        if (stage.FallbackReason is "ep_unavailable" or "ep_not_ready" or "partial_offload_required")
        {
            return true;
        }

        string requested = NormalizeExecutionProviderToken(stage.RequestedExecutionProvider);
        string resolved = NormalizeExecutionProviderToken(stage.ResolvedExecutionProvider);
        return !string.Equals(requested, "cpu", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(requested, "auto", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(requested, resolved, StringComparison.OrdinalIgnoreCase);
    }

    internal static GpuHardwareVendor DetectGpuVendor(HardwareProfile hardware)
    {
        string? description = hardware.GpuDescription;
        if (string.IsNullOrWhiteSpace(description))
        {
            return GpuHardwareVendor.Unknown;
        }

        if (description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return GpuHardwareVendor.Nvidia;
        }

        if (description.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
        {
            return GpuHardwareVendor.Amd;
        }

        if (description.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return GpuHardwareVendor.Intel;
        }

        if (description.Contains("Qualcomm", StringComparison.OrdinalIgnoreCase) ||
            description.Contains("Adreno", StringComparison.OrdinalIgnoreCase))
        {
            return GpuHardwareVendor.Qualcomm;
        }

        return GpuHardwareVendor.Unknown;
    }

    private static string NormalizeExecutionProviderToken(string token) =>
        token.Trim().ToLowerInvariant() switch
        {
            "dml" => "directml",
            "tensorrt-rtx" => "trt-rtx",
            _ => token.Trim().ToLowerInvariant(),
        };

    internal enum GpuHardwareVendor
    {
        Unknown,
        Nvidia,
        Amd,
        Intel,
        Qualcomm,
    }
}
