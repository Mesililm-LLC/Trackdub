using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.StarterPacks;

public enum TrtRtxStarterPackSmokeTargetStatus
{
    Passed,
    Failed,
    Skipped,
}

public sealed record TrtRtxStarterPackSmokeTargetResult(
    string Label,
    string ModelReference,
    TrtRtxStarterPackSmokeTargetStatus Status,
    string? Detail = null);

public sealed record TrtRtxStarterPackSmokeReport(
    int Attempted,
    int Passed,
    int Failed,
    int Skipped,
    IReadOnlyList<TrtRtxStarterPackSmokeTargetResult> Targets)
{
    public bool HasFailures => Failed > 0;

    public bool HasAttempts => Attempted > 0;
}

/// <summary>
/// Runs planner-style ONNX smoke tests for starter-pack turbo TRT RTX targets.
/// </summary>
public static class TrtRtxStarterPackSmokeRunner
{
    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            new OnnxExecutionProviderSmokeTester(),
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            progress: null,
            cancellationToken);

    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            new OnnxExecutionProviderSmokeTester(),
            targets,
            progress: null,
            cancellationToken);

    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets,
        IProgress<TrtRtxStarterPackSmokeTargetResult>? progress,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            new OnnxExecutionProviderSmokeTester(),
            targets,
            progress,
            cancellationToken);

    public static Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IExecutionProviderSmokeTester smokeTester,
        CancellationToken cancellationToken = default) =>
        RunAsync(
            modelCacheDirectory,
            smokeTester,
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            progress: null,
            cancellationToken);

    public static async Task<TrtRtxStarterPackSmokeReport> RunAsync(
        string? modelCacheDirectory,
        IExecutionProviderSmokeTester smokeTester,
        IReadOnlyList<TrtRtxSmokeCatalog.Target> targets,
        IProgress<TrtRtxStarterPackSmokeTargetResult>? progress = null,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind provider = ExecutionProviderKind.TensorRTRtx)
    {
        ArgumentNullException.ThrowIfNull(smokeTester);
        ArgumentNullException.ThrowIfNull(targets);

        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? manifestError))
        {
            throw new InvalidOperationException(manifestError ?? "Bundled model manifest was not found.");
        }

        var resolver = new BenchmarkModelPathResolver(registry, modelCacheDirectory);
        var results = new List<TrtRtxStarterPackSmokeTargetResult>(targets.Count);
        int attempted = 0;
        int passed = 0;
        int failed = 0;
        int skipped = 0;

        // Results are reported as they complete so a fatal native crash mid-catalog
        // (e.g. a provider that terminates the process) does not lose earlier results.
        void AddResult(TrtRtxStarterPackSmokeTargetResult result)
        {
            results.Add(result);
            progress?.Report(result);
        }

        foreach (TrtRtxSmokeCatalog.Target target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BenchmarkModelCandidate candidate;
            try
            {
                candidate = resolver.ResolveSingle(target.ModelReference, target.Variant);
            }
            catch (FileNotFoundException ex)
            {
                skipped++;
                AddResult(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Skipped,
                    ex.Message));
                continue;
            }

            BundledModelManifestEntry? entry = registry!.Entries.FirstOrDefault(
                model => model.ModelId.Equals(target.ModelReference, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                failed++;
                AddResult(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    $"Manifest entry missing for '{target.ModelReference}'."));
                continue;
            }

            string modelRootPath = ResolveSmokeModelRootPath(candidate);

            string variantAlias = target.Variant
                ?? candidate.VariantAlias
                ?? "default";

            RuntimeStage stage;
            try
            {
                stage = ResolveStage(entry);
            }
            catch (ArgumentOutOfRangeException ex)
            {
                failed++;
                AddResult(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    ex.Message));
                continue;
            }

            var request = new ExecutionProviderSmokeTestRequest(
                stage,
                entry.ModelId,
                entry.Aliases[0],
                entry.EngineFamily,
                variantAlias,
                provider,
                modelRootPath,
                candidate.ModelPath);

            attempted++;
            try
            {
                ExecutionProviderSmokeTestResult smokeResult = await smokeTester
                    .SmokeTestAsync(request, cancellationToken)
                    .ConfigureAwait(false);

                if (smokeResult.Passed)
                {
                    passed++;
                    AddResult(new TrtRtxStarterPackSmokeTargetResult(
                        target.Label,
                        target.ModelReference,
                        TrtRtxStarterPackSmokeTargetStatus.Passed));
                }
                else
                {
                    failed++;
                    AddResult(new TrtRtxStarterPackSmokeTargetResult(
                        target.Label,
                        target.ModelReference,
                        TrtRtxStarterPackSmokeTargetStatus.Failed,
                        smokeResult.Detail ?? "smoke test failed"));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                AddResult(new TrtRtxStarterPackSmokeTargetResult(
                    target.Label,
                    target.ModelReference,
                    TrtRtxStarterPackSmokeTargetStatus.Failed,
                    string.IsNullOrWhiteSpace(ex.Message) ? "smoke test failed" : ex.Message));
            }
        }

        return new TrtRtxStarterPackSmokeReport(attempted, passed, failed, skipped, results);
    }

    /// <summary>
    /// Runs the real, production-parity TRT RTX smoke path (same <see cref="OnnxExecutionProviderSmokeTester"/>
    /// code the runtime uses) against an explicit ONNX entry path instead of the model cache — for an
    /// Olive-recipe-staged output directory an Olive validator script just produced, before that
    /// output is trusted. Confirms the model actually loads and the effective provider is the
    /// requested one (TensorRT RTX by default), not a silent CPU/DirectML fallback (see <see cref="ExecutionProviderSmokeTestResult"/>).
    /// </summary>
    public static async Task<TrtRtxStarterPackSmokeTargetResult> VerifyEntryPathAsync(
        string modelId,
        string entryPath,
        string? variant = null,
        IExecutionProviderSmokeTester? smokeTester = null,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind provider = ExecutionProviderKind.TensorRTRtx)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entryPath);

        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? manifestError))
        {
            throw new InvalidOperationException(manifestError ?? "Bundled model manifest was not found.");
        }

        BundledModelManifestEntry? entry = registry!.Entries.FirstOrDefault(
            model => model.ModelId.Equals(modelId, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            throw new InvalidOperationException($"Manifest entry missing for '{modelId}'.");
        }

        string absoluteEntryPath = Path.GetFullPath(entryPath);
        if (!File.Exists(absoluteEntryPath))
        {
            throw new FileNotFoundException($"Entry path not found: '{absoluteEntryPath}'.", absoluteEntryPath);
        }

        string modelRootPath = Path.GetDirectoryName(absoluteEntryPath)
            ?? throw new InvalidOperationException($"Could not resolve model root for '{absoluteEntryPath}'.");

        RuntimeStage stage = ResolveStage(entry);
        string alias = entry.Aliases.Count > 0 ? entry.Aliases[0] : entry.ModelId;

        var request = new ExecutionProviderSmokeTestRequest(
            stage,
            entry.ModelId,
            alias,
            entry.EngineFamily,
            variant ?? "default",
            provider,
            modelRootPath,
            absoluteEntryPath);

        IExecutionProviderSmokeTester tester = smokeTester ?? new OnnxExecutionProviderSmokeTester();
        ExecutionProviderSmokeTestResult result = await tester
            .SmokeTestAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return new TrtRtxStarterPackSmokeTargetResult(
            alias,
            entry.ModelId,
            result.Passed ? TrtRtxStarterPackSmokeTargetStatus.Passed : TrtRtxStarterPackSmokeTargetStatus.Failed,
            result.Detail);
    }

    private static RuntimeStage ResolveStage(BundledModelManifestEntry entry)
    {
        if (entry.EngineFamily.Equals("phi-genai", StringComparison.OrdinalIgnoreCase)
            || entry.EngineFamily.Equals("qwen-instruct", StringComparison.OrdinalIgnoreCase))
        {
            return RuntimeStage.TextRefinement;
        }

        return entry.Task.Trim().ToLowerInvariant() switch
        {
            "vad" => RuntimeStage.Vad,
            "asr" => RuntimeStage.Asr,
            "translation" => RuntimeStage.Translation,
            "tts" => RuntimeStage.Tts,
            "diarization" => RuntimeStage.Diarization,
            "separation" => RuntimeStage.Separation,
            "speech-enhancement" => RuntimeStage.SpeechEnhancement,
            "forced-alignment" => RuntimeStage.LipSync,
            "text-refinement" => RuntimeStage.TextRefinement,
            "overlap-rescue" => RuntimeStage.OverlapRescue,
            "lip-synthesis" => RuntimeStage.LipSynthesis,
            "face-detection" or "face-landmarks" => RuntimeStage.LipSynthesis,
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Task, "Unknown TRT RTX smoke task."),
        };
    }

    private static string ResolveSmokeModelRootPath(BenchmarkModelCandidate candidate)
    {
        if (string.Equals(Path.GetFileName(candidate.ModelPath), "genai_config.json", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(candidate.ModelPath)
                ?? throw new InvalidOperationException($"Could not resolve GenAI model root for '{candidate.ModelPath}'.");
        }

        return candidate.RootDirectory
            ?? Path.GetDirectoryName(candidate.ModelPath)
            ?? throw new InvalidOperationException($"Could not resolve model root for '{candidate.DisplayName}'.");
    }
}
