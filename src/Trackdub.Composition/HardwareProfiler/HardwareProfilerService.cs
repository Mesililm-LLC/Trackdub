using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Runtime.Planning;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif

namespace Trackdub.Composition.HardwareProfiler;

public sealed class HardwareProfilerService(
    IHardwareProfileProvider hardwareProfileProvider,
    BenchmarkModelPathResolver modelPathResolver,
    JsonHardwareProfilerStore profilerStore,
    HardwareProfilerHistoryRecorder historyRecorder,
    IStudioSettingsService studioSettingsService,
    IAppStoragePaths storagePaths,
    IModelBenchmarkRunner? benchmarkRunner = null) : IHardwareProfilerService
{
    private const int BenchmarkRunCount = 3;

    public async Task<HardwareProfilerViewState> GetViewStateAsync(CancellationToken cancellationToken = default)
    {
        StudioSettings settings = await studioSettingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await historyRecorder.EnsureLegacyImportAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Best-effort one-shot legacy import; JSON latest snapshot remains profiler authority.
        }

        HardwareProfilerSnapshot? snapshot = await profilerStore.LoadLatestAsync(cancellationToken).ConfigureAwait(false);
        HardwareFingerprint currentFingerprint = await CreateFingerprintAsync(cancellationToken).ConfigureAwait(false);
        bool isStale = snapshot is not null &&
                       !string.Equals(snapshot.Fingerprint.Hash, currentFingerprint.Hash, StringComparison.Ordinal);

        HardwarePresetRecommendation? recommendation = ResolveRecommendation(settings, snapshot, isStale);
        HardwareQualityPreset effectivePreset = recommendation?.Preset ?? HardwareQualityPreset.Balanced;

        return new HardwareProfilerViewState(
            snapshot,
            isStale,
            effectivePreset,
            recommendation,
            settings.HardwareQualityPresetOverrideKey,
            !string.IsNullOrWhiteSpace(settings.HardwareQualityPresetOverrideKey),
            isStale ? null : settings.HardwareProfilerEvidenceId ?? snapshot?.EvidenceId.ToString());
    }

    public async Task<HardwareProfilerRunResult> RunBenchmarkSuiteAsync(CancellationToken cancellationToken = default)
    {
        if (benchmarkRunner is null)
        {
            return HardwareProfilerRunResult.Failure(
                "Stage benchmarks require the Windows ONNX benchmark runner. Run Trackdub on Windows with bundled models installed.");
        }

        StudioSettings settings = await studioSettingsService.LoadAsync(cancellationToken).ConfigureAwait(false);

        HardwareFingerprint fingerprint = await CreateFingerprintAsync(cancellationToken).ConfigureAwait(false);
        string reportsRoot = Path.Join(storagePaths.UserDataRoot, "hardware-profiler", "reports", fingerprint.Hash[..12]);
        Directory.CreateDirectory(reportsRoot);

        string? policyKey = WindowsMlExecutionDevicePolicySettings.ToKey(settings.WindowsMlExecutionDevicePolicy);
        BenchmarkProviderPreference benchmarkProviderPreference =
            ResolveProfilerBenchmarkProviderPreference(fingerprint);
        List<StageBenchmarkScenarioResult> scenarioResults = [];

        foreach (StageBenchmarkModelCatalog.ScenarioDefinition definition in StageBenchmarkModelCatalog.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            scenarioResults.Add(await RunScenarioAsync(
                definition,
                reportsRoot,
                policyKey,
                benchmarkProviderPreference,
                cancellationToken).ConfigureAwait(false));
        }

        HardwarePresetRecommendation recommendation =
            HardwarePresetRecommendationEngine.Recommend(fingerprint, scenarioResults);

        HardwareProfilerSnapshot snapshot = HardwareProfilerSnapshot.Create(fingerprint, scenarioResults, recommendation);
        await profilerStore.SaveLatestAsync(snapshot, cancellationToken).ConfigureAwait(false);
        await historyRecorder.RecordSnapshotAsync(snapshot, reportsRoot, BenchmarkRunCount, cancellationToken)
            .ConfigureAwait(false);

        StudioSettings updatedSettings = settings with
        {
            HardwareProfilerEvidenceId = snapshot.EvidenceId.ToString(),
            HardwareProfilerFingerprint = fingerprint.Hash,
            ModelTierPreference = string.IsNullOrWhiteSpace(settings.HardwareQualityPresetOverrideKey)
                ? recommendation.SuggestedModelTierPreference
                : settings.ModelTierPreference
        };

        await studioSettingsService.SaveAsync(updatedSettings, cancellationToken).ConfigureAwait(false);
        return HardwareProfilerRunResult.Success(snapshot);
    }

    public string ResolveEffectiveModelTierPreference(StudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        if (!string.IsNullOrWhiteSpace(settings.HardwareQualityPresetOverrideKey) &&
            HardwarePresetRecommendation.TryParseSettingsKey(settings.HardwareQualityPresetOverrideKey, out HardwareQualityPreset overridePreset))
        {
            return HardwarePresetRecommendation.ToModelTierPreference(overridePreset);
        }

        return settings.ModelTierPreference;
    }

    private static HardwarePresetRecommendation? ResolveRecommendation(
        StudioSettings settings,
        HardwareProfilerSnapshot? snapshot,
        bool isStale)
    {
        if (!string.IsNullOrWhiteSpace(settings.HardwareQualityPresetOverrideKey) &&
            HardwarePresetRecommendation.TryParseSettingsKey(settings.HardwareQualityPresetOverrideKey, out HardwareQualityPreset overridePreset))
        {
            return new HardwarePresetRecommendation(
                overridePreset,
                "Manual preset override",
                ["User preset override is active."],
                HardwarePresetRecommendation.ToModelTierPreference(overridePreset));
        }

        if (snapshot is null || isStale)
        {
            return null;
        }

        return snapshot.Recommendation;
    }

    private async Task<HardwareFingerprint> CreateFingerprintAsync(CancellationToken cancellationToken)
    {
        HardwareProfile profile = await hardwareProfileProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        long? totalRamBytes = profile.TotalRamMb > 0 ? profile.TotalRamMb * 1024L * 1024L : null;
        long? vramBytes = profile.DedicatedVramMb > 0 ? profile.DedicatedVramMb * 1024L * 1024L : null;

        return HardwareFingerprint.Create(
            profile.OperatingSystem,
            profile.Architecture,
            profile.GpuDescription,
            totalRamBytes,
            vramBytes);
    }

    internal static BenchmarkProviderPreference ResolveProfilerBenchmarkProviderPreference(
        HardwareFingerprint fingerprint,
        bool? isWindows = null)
    {
        bool runningOnWindows = isWindows ?? OperatingSystem.IsWindows();
        if (runningOnWindows &&
            !string.IsNullOrWhiteSpace(fingerprint.GpuDescription) &&
            fingerprint.GpuDescription.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return BenchmarkProviderPreference.TensorRtRtx;
        }

        return BenchmarkProviderPreference.Auto;
    }

    private async Task<StageBenchmarkScenarioResult> RunScenarioAsync(
        StageBenchmarkModelCatalog.ScenarioDefinition definition,
        string reportsRoot,
        string? policyKey,
        BenchmarkProviderPreference providerPreference,
        CancellationToken cancellationToken)
    {
        foreach (string alias in definition.ModelAliases)
        {
            BenchmarkModelResolutionResult resolution = modelPathResolver.Discover(alias);
            BenchmarkModelCandidate? candidate = ResolveCandidate(resolution);
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.ModelPath))
            {
                continue;
            }

            string modelPath = candidate.ModelPath;
            string reportPath = Path.Join(reportsRoot, definition.ScenarioName + "-" + Path.GetFileNameWithoutExtension(modelPath) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);

            try
            {
                BenchmarkReport report = await benchmarkRunner!.RunAsync(
                    new BenchmarkRequest(modelPath, reportPath, providerPreference, BenchmarkRunCount, policyKey),
                    cancellationToken).ConfigureAwait(false);

                return new StageBenchmarkScenarioResult(
                    definition.Scenario,
                    report.Status,
                    report.RequestedProvider,
                    report.SelectedProvider,
                    report.Measurements.WarmLatencyAverageMilliseconds,
                    report.Measurements.RealTimeFactorAverage,
                    report.FailureReason);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new StageBenchmarkScenarioResult(
                    definition.Scenario,
                    BenchmarkStatus.Failed,
                    FormatBenchmarkProviderLabel(providerPreference),
                    "unknown",
                    null,
                    null,
                    ex.Message);
            }
        }

        return new StageBenchmarkScenarioResult(
            definition.Scenario,
            BenchmarkStatus.Failed,
            FormatBenchmarkProviderLabel(providerPreference),
            "unknown",
            null,
            null,
            "No bundled model found for " + definition.ScenarioName + ".");
    }

    private static string FormatBenchmarkProviderLabel(BenchmarkProviderPreference preference) =>
        preference switch
        {
            BenchmarkProviderPreference.Auto => "auto",
            BenchmarkProviderPreference.Cpu => "cpu",
            BenchmarkProviderPreference.Dml => "dml",
            BenchmarkProviderPreference.TensorRtRtx => "trt-rtx",
            BenchmarkProviderPreference.Migraphx => "migraphx",
            BenchmarkProviderPreference.Cuda => "cuda",
            BenchmarkProviderPreference.TensorRt => "tensorrt",
            BenchmarkProviderPreference.Qnn => "qnn",
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown provider preference.")
        };

    private static BenchmarkModelCandidate? ResolveCandidate(BenchmarkModelResolutionResult resolution)
    {
        if (resolution.Candidates.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(resolution.DefaultCandidateKey))
        {
            BenchmarkModelCandidate? selected = resolution.Candidates.FirstOrDefault(candidate =>
                candidate.CandidateKey.Equals(resolution.DefaultCandidateKey, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return selected;
            }
        }

        return resolution.Candidates[0];
    }
}
