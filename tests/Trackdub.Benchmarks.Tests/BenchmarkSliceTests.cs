using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using Trackdub.Benchmarks;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;
#if WINDOWS
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
using Microsoft.ML.OnnxRuntime;
#endif

namespace Trackdub.Benchmarks.Tests;

public sealed class BenchmarkOptionsTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [WindowsOnlyFact]
    public void TryParse_ParsesExpectedValues()
    {
        var args = new[]
        {
            "--model", ".\\model.onnx",
            "--output", ".\\out\\report.json",
            "--provider", "auto",
            "--runs", "7",
            "--format", "json"
        };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.False(options.ShowHelp);
        Assert.Equal(BenchmarkProviderPreference.Auto, options.ProviderPreference);
        Assert.Equal(7, options.RunCount);
        Assert.Equal(ReportFormat.Json, options.ReportFormat);
        Assert.EndsWith(Path.Join("out", "report.json"), options.OutputPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryParse_ParsesTrtRtxSmokeScope()
    {
        var args = new[] { "--scope", TrtRtxSmokeCatalog.ScopeName, "--provider", "trt-rtx", "--runs", "1" };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(TrtRtxSmokeCatalog.ScopeName, options.Scope);
        Assert.Equal(string.Empty, options.ModelPath);
        Assert.Equal(BenchmarkProviderPreference.TensorRtRtx, options.ProviderPreference);
    }

    [Fact]
    public void TryParse_DefaultRun_AppliesStandardSamplingPolicy()
    {
        var args = new[] { "--model", "model.onnx" };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(10, options.RunCount);
        Assert.NotNull(options.Execution);
        Assert.Equal(3000, options.Execution!.MinimumMeasurementMilliseconds);
        Assert.Equal(1, options.Execution.MinimumWarmupRuns);
        Assert.Equal(200, options.Execution.MinimumWarmupMilliseconds);
        Assert.Equal(BenchmarkInputMode.Synthetic, options.Execution.InputMode);
        Assert.Equal(42, options.Execution.InputSeed);
    }

    [Fact]
    public void TryParse_ExplicitRunsWithoutDuration_PreservesExactCount()
    {
        var args = new[] { "--model", "model.onnx", "--runs", "1" };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(1, options.RunCount);
        Assert.Equal(0, options.Execution!.MinimumMeasurementMilliseconds);
    }

    [Fact]
    public void TryParse_DurationWithoutRuns_KeepsSampleMinimumAndDurationFloor()
    {
        var args = new[] { "--model", "model.onnx", "--duration-ms", "1500" };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(10, options.RunCount);
        Assert.Equal(1500, options.Execution!.MinimumMeasurementMilliseconds);
    }

    [Fact]
    public void TryParse_RunsAndDuration_TreatsRunsAsMinimumWithFloor()
    {
        var args = new[] { "--model", "model.onnx", "--runs", "3", "--duration-ms", "500" };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(3, options.RunCount);
        Assert.Equal(500, options.Execution!.MinimumMeasurementMilliseconds);
    }

    [Fact]
    public void TryParse_ParsesNewExecutionFlags()
    {
        var args = new[]
        {
            "--model", "model.onnx",
            "--warmup-runs", "3", "--warmup-ms", "0",
            "--input-mode", "zeros", "--input-seed", "7",
            "--io-mode", "device-bound",
            "--cuda-graphs", "on",
            "--artifact", "require",
            "--runtime-cache", "isolated",
            "--runtime-cache-dir", "cache-dir",
            "--history"
        };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(3, options.Execution!.MinimumWarmupRuns);
        Assert.Equal(0, options.Execution.MinimumWarmupMilliseconds);
        Assert.Equal(BenchmarkInputMode.Zeros, options.Execution.InputMode);
        Assert.Equal(7, options.Execution.InputSeed);
        Assert.Equal(BenchmarkIoMode.DeviceBound, options.Execution.IoMode);
        Assert.True(options.Execution.EffectiveTensorRtRtx.EnableCudaGraph);
        Assert.Equal(BenchmarkArtifactMode.Require, options.Execution.EffectiveTensorRtRtx.ArtifactMode);
        Assert.Equal(BenchmarkRuntimeCacheMode.Isolated, options.Execution.EffectiveTensorRtRtx.CacheMode);
        Assert.Equal("cache-dir", options.Execution.EffectiveTensorRtRtx.RuntimeCacheDirectory);
        Assert.True(options.HistoryRequested);
        Assert.Equal(BenchmarkCudaGraphMode.On, options.CudaGraphMode);
    }

    [Fact]
    public void TryParse_CudaGraphsDefaultsToAutoAndParsesAuto()
    {
        bool defaultSuccess = BenchmarkOptions.TryParse(
            ["--model", "model.onnx"], TextWriter.Null, out BenchmarkOptions defaults);
        Assert.True(defaultSuccess);
        Assert.Equal(BenchmarkCudaGraphMode.Auto, defaults.CudaGraphMode);
        Assert.Equal(BenchmarkCudaGraphMode.Auto, defaults.Execution!.EffectiveTensorRtRtx.CudaGraphMode);
        Assert.False(defaults.Execution.EffectiveTensorRtRtx.EnableCudaGraph);

        bool success = BenchmarkOptions.TryParse(
            ["--model", "model.onnx", "--cuda-graphs", "auto"], TextWriter.Null, out BenchmarkOptions options);
        Assert.True(success);
        Assert.Equal(BenchmarkCudaGraphMode.Auto, options.CudaGraphMode);
    }

    [Fact]
    public void TryParse_ReferenceToolFlags()
    {
        var args = new[]
        {
            "--model", "model.onnx",
            "--reference-tool", "tensorrt-rtx",
            "--reference-tool-path", "C:\\tools\\tensorrt_rtx.exe",
            "--reference-tool-required"
        };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(BenchmarkReferenceToolKind.TensorRtRtx, options.ReferenceTool);
        Assert.Equal("C:\\tools\\tensorrt_rtx.exe", options.ReferenceToolPath);
        Assert.True(options.ReferenceToolRequired);
    }

    [Fact]
    public void TryParse_ReferenceToolRequiredWithoutTool_Fails()
    {
        var args = new[] { "--model", "model.onnx", "--reference-tool-required" };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParse_InputShapeOverridesAndProfileMode()
    {
        var args = new[]
        {
            "--model", "model.onnx",
            "--input-mode", "profile",
            "--input-shape", "chunk=1,3040,128",
            "--input-shape", "spkcache=1,188,512"
        };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out BenchmarkOptions options);

        Assert.True(success);
        Assert.Equal(BenchmarkInputMode.Profile, options.Execution!.InputMode);
        Assert.NotNull(options.Execution.InputShapeOverrides);
        Assert.Equal([1, 3040, 128], options.Execution.InputShapeOverrides!["chunk"]);
        Assert.Equal([1, 188, 512], options.Execution.InputShapeOverrides["spkcache"]);
    }

    [Theory]
    [InlineData("--duration-ms", "-1")]
    [InlineData("--warmup-runs", "0")]
    [InlineData("--warmup-ms", "-5")]
    [InlineData("--input-mode", "bogus")]
    [InlineData("--input-shape", "chunk")]
    [InlineData("--input-shape", "=1,2")]
    [InlineData("--input-shape", "chunk=1,0,128")]
    [InlineData("--io-mode", "bogus")]
    [InlineData("--cuda-graphs", "bogus")]
    [InlineData("--artifact", "bogus")]
    [InlineData("--runtime-cache", "bogus")]
    [InlineData("--reference-tool", "bogus")]
    public void TryParse_InvalidNewFlagValues_Fail(string flag, string value)
    {
        var args = new[] { "--model", "model.onnx", flag, value };

        bool success = BenchmarkOptions.TryParse(args, TextWriter.Null, out _);

        Assert.False(success);
    }

    [Fact]
    public void TrtRtxSmokeCatalog_includes_starter_pack_turbo_models()
    {
        Assert.Contains(
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            target => target.ModelReference.Contains("madlad400", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            target => target.ModelReference.Contains("whisper-small", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            target => target.ModelReference.Contains("qwen3-asr-0.6b", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            target => target.ModelReference.Contains("qwen3-asr-1.7b", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TrtRtxSmokeWorkflow_downloads_every_starter_pack_turbo_target()
    {
        string workflow = ReadTrtRtxSmokeWorkflow();

        // The workflow must populate the cache for every smoke target before running the
        // benchmark; otherwise BenchmarkModelPathResolver skips them all and the smoke run
        // exits non-zero. This guards against the regression where the download step was absent.
        Assert.Contains("models download", workflow, StringComparison.Ordinal);

        // Parse the pwsh hashtable target entries the workflow loops over, e.g.
        //   @{ Id = 'microsoft/Phi-4-mini-instruct-onnx'; Variant = 'gpu-int4' }
        //   @{ Id = 'openai/whisper-small' }
        // Presence-only checks would pass even if an id were paired with the wrong variant
        // or a stale target lingered after a catalog removal, so match each id to the variant
        // declared on the same entry and assert the set matches the catalog exactly.
        IReadOnlyDictionary<string, string?> workflowTargets = ParseWorkflowDownloadTargets(workflow);

        Assert.Equal(TrtRtxSmokeCatalog.StarterPackTurboGpu.Count, workflowTargets.Count);

        foreach (TrtRtxSmokeCatalog.Target target in TrtRtxSmokeCatalog.StarterPackTurboGpu)
        {
            Assert.True(
                workflowTargets.TryGetValue(target.ModelReference, out string? workflowVariant),
                $"Workflow is missing a download entry for '{target.ModelReference}'.");

            string? expectedVariant = string.IsNullOrWhiteSpace(target.Variant) ? null : target.Variant;
            Assert.True(
                string.Equals(expectedVariant, workflowVariant, StringComparison.Ordinal),
                $"Workflow entry for '{target.ModelReference}' declares variant "
                    + $"'{workflowVariant ?? "<none>"}' but the catalog expects "
                    + $"'{expectedVariant ?? "<none>"}'.");
        }
    }

    private static IReadOnlyDictionary<string, string?> ParseWorkflowDownloadTargets(string workflow)
    {
        // Match each `@{ Id = '<id>' [; Variant = '<variant>'] }` entry, capturing the id and,
        // when present, the variant declared on the same logical entry.
        var entryPattern = new System.Text.RegularExpressions.Regex(
            @"@\{\s*Id\s*=\s*'(?<id>[^']+)'(?:\s*;\s*Variant\s*=\s*'(?<variant>[^']+)')?\s*\}",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        var targets = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match match in entryPattern.Matches(workflow))
        {
            string id = match.Groups["id"].Value;
            string? variant = match.Groups["variant"].Success ? match.Groups["variant"].Value : null;
            Assert.False(
                targets.ContainsKey(id),
                $"Workflow lists '{id}' more than once in the download loop.");
            targets.Add(id, variant);
        }

        return targets;
    }

    [Fact]
    public void TrtRtxSmokeWorkflow_does_not_mask_smoke_failures()
    {
        string workflow = ReadTrtRtxSmokeWorkflow();

        // continue-on-error at the job level (or a per-step masker) would hide the benchmark's
        // non-zero all-skipped exit, so the run would report green even when nothing ran.
        Assert.DoesNotContain("continue-on-error", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("|| true", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void TrtRtxSmokeWorkflow_keeps_model_cache_derived_from_cache_root()
    {
        string workflow = ReadTrtRtxSmokeWorkflow();

        // The CLI writes to TRACKDUB_CACHE_ROOT/model-cache while the benchmark reads
        // TRACKDUB_MODEL_CACHE, so the two steps only agree while
        // TRACKDUB_MODEL_CACHE == TRACKDUB_CACHE_ROOT/model-cache. GitHub Actions cannot
        // reference a sibling env var within the same env map, so the values are set by hand;
        // this guard fails if a future edit changes one without the other and silently
        // reintroduces the all-skipped result.
        string cacheRoot = ReadJobEnvValue(workflow, "TRACKDUB_CACHE_ROOT");
        string modelCache = ReadJobEnvValue(workflow, "TRACKDUB_MODEL_CACHE");

        Assert.Equal(cacheRoot + "/model-cache", modelCache);
    }

    private static string ReadJobEnvValue(string workflow, string name)
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            $@"^\s*{System.Text.RegularExpressions.Regex.Escape(name)}\s*:\s*(?<value>\S.*?)\s*$",
            System.Text.RegularExpressions.RegexOptions.Multiline
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        System.Text.RegularExpressions.Match match = pattern.Match(workflow);
        Assert.True(match.Success, $"Workflow does not define env var '{name}'.");

        return match.Groups["value"].Value.Trim().Trim('\'', '"');
    }

    private static string ReadTrtRtxSmokeWorkflow()
    {
        string workflowPath = ResolveRepositoryFile(".github/workflows/trt-rtx-smoke.yml");
        return File.ReadAllText(workflowPath);
    }

    private static string ResolveRepositoryFile(string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException(
                $"Expected a relative path, but got rooted path '{relativePath}'.",
                nameof(relativePath));
        }

        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Join(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate '{relativePath}' walking up from '{AppContext.BaseDirectory}'.");
    }

    [Fact]
    public void TrtRtxSmokeCatalog_remaining_includes_untested_onnx_gpu_models()
    {
        Assert.Contains(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Contains("nemotron", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Contains("LatentSync", StringComparison.OrdinalIgnoreCase));
        // Silero VAD joined the catalog for the 2026-10-18 TensorRT qualification retest;
        // it stays smokeable here even though the planner excludes both TensorRT providers
        // for it, so a future shape-inferred export can be re-qualified with the same target.
        Assert.Contains(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Contains("silero", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Contains("Kokoro", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Equals("google/madlad400-3b-mt", StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Variant, "trt-fp16", StringComparison.OrdinalIgnoreCase));
        // CosyVoice int8/int4 quantized graphs are CUDA-route only: the TRT-RTX
        // Myelin builder rejects their int8 dtype (verified live 2026-10-17), so
        // they must not be offered as TRT-RTX smoke targets.
        Assert.DoesNotContain(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Equals("tonythethompson/CosyVoice-300M-ONNX", StringComparison.OrdinalIgnoreCase)
                && target.Variant is not null);

        // GPU-only invariant: Phi-3.5 ships only a cpu-int4 bundle, so it must not appear here.
        Assert.DoesNotContain(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Contains("Phi-3.5-mini-instruct-onnx", StringComparison.OrdinalIgnoreCase));

        // microsoft/phi-4-onnx was dropped from RemainingOnnxGpu (superseded by the
        // starter-pack Phi-4-mini-instruct-onnx gpu-int4 target). Remaining must not
        // resurrect it as a cpu-int4 entry.
        Assert.DoesNotContain(
            TrtRtxSmokeCatalog.RemainingOnnxGpu,
            target => target.ModelReference.Equals("microsoft/phi-4-onnx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            TrtRtxSmokeCatalog.StarterPackTurboGpu,
            target => target.ModelReference.Contains("Phi-4-mini-instruct-onnx", StringComparison.OrdinalIgnoreCase)
                && string.Equals(target.Variant, "gpu-int4", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryParse_ParsesTensorRtRtxProviderAlias()
    {
        var args = new[] { "--model", ".\\model.onnx", "--provider", "trt-rtx" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal(BenchmarkProviderPreference.TensorRtRtx, options.ProviderPreference);
    }

    [Fact]
    public void TryParse_ParsesQnnProvider()
    {
        var args = new[] { "--model", ".\\model.onnx", "--provider", "qnn" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal(BenchmarkProviderPreference.Qnn, options.ProviderPreference);
    }

    [Fact]
    public void TryParse_WhenWindowsMlDevicePolicyUnknown_ReturnsFalse()
    {
        var error = new StringWriter();
        string[] args =
        [
            "--model", "model.onnx",
            "--windows-ml-device-policy", "max-performnce"
        ];

        bool parsed = BenchmarkOptions.TryParse(args, error, out BenchmarkOptions? options);

        Assert.False(parsed);
        Assert.NotNull(options);
        Assert.True(options!.ShowHelp);
        Assert.Contains("Unknown Windows ML device policy", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WindowsMlExecutionDevicePolicySettings.ExplicitKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MaxPerformanceKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.PreferNpuKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MaxEfficiencyKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MinOverallPowerKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.DefaultRenderKey)]
    [InlineData(WindowsMlExecutionDevicePolicySettings.MinPowerKey)]
    public void TryParse_WhenWindowsMlDevicePolicyKnown_AcceptsKey(string policyKey)
    {
        var error = new StringWriter();
        string[] args =
        [
            "--model", "model.onnx",
            "--windows-ml-device-policy", policyKey
        ];

        bool parsed = BenchmarkOptions.TryParse(args, error, out BenchmarkOptions? options);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.False(options!.ShowHelp);
        Assert.Equal(policyKey, options.WindowsMlDevicePolicyKey);
    }

    [Fact]
    public void TryParse_PreservesModelScopeReference()
    {
        var args = new[] { "--model", "silero-vad" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal("silero-vad", options.ModelPath);
    }

    [Fact]
    public void TryParse_ParsesVariantOption()
    {
        var args = new[] { "--model", "silero-vad", "--variant", "q4" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out var options);

        Assert.True(success);
        Assert.Equal("q4", options.Variant);
        Assert.False(options.AllVariants);
    }

    [Fact]
    public void TryParse_RejectsVariantAndAllVariants()
    {
        var args = new[] { "--model", "silero-vad", "--variant", "q4", "--all-variants" };

        var success = BenchmarkOptions.TryParse(args, TextWriter.Null, out _);

        Assert.False(success);
    }

#if WINDOWS
    [RequiresBundledModelFact("silero-vad/onnx/model_q4.onnx")]
    public async Task ProgramRunAsync_HonorsEmbeddedVariantReference()
    {
        string reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = await Program.RunAsync(
                ["--model", "silero-vad@q4", "--runs", "1", "--output", reportPath, "--format", "json"],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);

            await using FileStream reportStream = File.OpenRead(reportPath);
            BenchmarkReport? report = await JsonSerializer.DeserializeAsync<BenchmarkReport>(reportStream, SerializerOptions);
            Assert.NotNull(report);
            Assert.EndsWith(Path.Join("models", "silero-vad", "onnx", "model_q4.onnx"), report!.ModelPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task ProgramRunAsync_PromptsForAmbiguousDirectoryAndStoresChoice()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Join(tempDirectory, "onnx");
        string variantAPath = Path.Join(onnxDirectory, "variant_a.onnx");
        string variantBPath = Path.Join(onnxDirectory, "variant_b.onnx");
        string reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        string defaultsPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        string? previousDefaultsPath = Environment.GetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH");

        Directory.CreateDirectory(onnxDirectory);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantAPath);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantBPath);
        Environment.SetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH", defaultsPath);

        try
        {
            var firstOutput = new StringWriter();
            var firstError = new StringWriter();

            int firstExitCode = await Program.RunAsync(
                ["--model", tempDirectory, "--runs", "1", "--output", reportPath, "--format", "json"],
                new StringReader("2" + Environment.NewLine),
                firstOutput,
                firstError,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, firstExitCode);
            Assert.Contains("Multiple benchmarkable ONNX variants were found", firstOutput.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(defaultsPath));
            string defaultsJson = await File.ReadAllTextAsync(defaultsPath);
            Assert.Contains("variant_b.onnx", defaultsJson, StringComparison.OrdinalIgnoreCase);

            var secondOutput = new StringWriter();
            var secondError = new StringWriter();
            int secondExitCode = await Program.RunAsync(
                ["--model", tempDirectory, "--runs", "1", "--output", reportPath, "--format", "json"],
                TextReader.Null,
                secondOutput,
                secondError,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, secondExitCode);
            Assert.DoesNotContain("Multiple benchmarkable ONNX variants were found", secondOutput.ToString(), StringComparison.Ordinal);

            await using FileStream reportStream = File.OpenRead(reportPath);
            BenchmarkReport? report = await JsonSerializer.DeserializeAsync<BenchmarkReport>(reportStream, SerializerOptions);
            Assert.NotNull(report);
            Assert.Equal(variantBPath, report!.ModelPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_BENCHMARK_DEFAULTS_PATH", previousDefaultsPath);
            File.Delete(reportPath);
            File.Delete(defaultsPath);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    // Gate on the TRT RTX smoke starter-pack models being present under the repo's
    // gitignored models/ directory. RunTrtRtxSmokeScopeAsync runs runner.RunAsync for
    // every TrtRtxSmokeCatalog.StarterPackTurboGpu target whose model resolves, so
    // without this gate the test would either exercise only the degenerate all-skipped
    // branch (no models cached) or run the full smoke sweep in a unit-test run (models
    // cached). With the gate it runs the real smoke deliberately or skips cleanly, in
    // line with the tests README policy against long model runs. The relative paths map
    // each StarterPackTurboGpu target to its manifest root_path + benchmark_entry.
    [RequiresBundledModelFact(
        "sortformer/cgus-diar_streaming_sortformer_4spk-v2.1-onnx/onnx/model.onnx",
        "whisper-small-genai/encoder.onnx",
        "whisper-medium-genai/encoder.onnx",
        "qwen3-asr-0.6b-onnx/encoder.onnx",
        "qwen3-asr-1.7b-onnx/encoder.onnx",
        "phi-4-mini-genai/cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4/genai_config.json",
        "madlad/google-madlad400-3b-mt/encoder_model_quantized.onnx",
        "chatterbox-turbo-onnx/onnx/language_model.onnx")]
    public async Task ProgramRunAsync_TrtRtxSmokeScope_WritesBatchReportPathInJsonMode()
    {
        string reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        string reportDirectory = Path.GetDirectoryName(reportPath)!;
        string reportNameWithoutExtension = Path.GetFileNameWithoutExtension(reportPath);
        string reportExtension = Path.GetExtension(reportPath);

        // Per-variant report paths written by DeriveVariantReportPath for each smoke
        // target that actually runs: "{output}-{label}{ext}" in the same directory.
        string[] variantReportPaths = TrtRtxSmokeCatalog.StarterPackTurboGpu
            .Select(target => Path.Join(
                reportDirectory,
                $"{reportNameWithoutExtension}-{target.Label}{reportExtension}"))
            .ToArray();

        var output = new StringWriter();
        var error = new StringWriter();

        try
        {
            int exitCode = await Program.RunAsync(
                [
                    "--scope", TrtRtxSmokeCatalog.ScopeName,
                    "--provider", "trt-rtx",
                    "--runs", "1",
                    "--output", reportPath,
                    "--format", "json"
                ],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Contains($"Batch report written to: {reportPath}", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(reportPath))
            {
                File.Delete(reportPath);
            }

            foreach (string variantReportPath in variantReportPaths)
            {
                if (File.Exists(variantReportPath))
                {
                    File.Delete(variantReportPath);
                }
            }
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task ProgramRunAsync_AllVariantsWritesAggregateAndPerVariantReports()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Join(tempDirectory, "onnx");
        string variantAPath = Path.Join(onnxDirectory, "variant_a.onnx");
        string variantBPath = Path.Join(onnxDirectory, "variant_b.onnx");
        string aggregateReportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Directory.CreateDirectory(onnxDirectory);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantAPath);
        File.Copy(OnnxModelBenchmarkRunnerTests.SampleModelPath, variantBPath);

        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            int exitCode = await Program.RunAsync(
                ["--model", tempDirectory, "--runs", "1", "--all-variants", "--output", aggregateReportPath, "--format", "json"],
                TextReader.Null,
                output,
                error,
                TestContext.Current.CancellationToken);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(aggregateReportPath));
            Assert.True(File.Exists(Path.Join(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_a{Path.GetExtension(aggregateReportPath)}")));
            Assert.True(File.Exists(Path.Join(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_b{Path.GetExtension(aggregateReportPath)}")));
        }
        finally
        {
            File.Delete(aggregateReportPath);
            File.Delete(Path.Join(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_a{Path.GetExtension(aggregateReportPath)}"));
            File.Delete(Path.Join(Path.GetDirectoryName(aggregateReportPath)!, $"{Path.GetFileNameWithoutExtension(aggregateReportPath)}-variant_b{Path.GetExtension(aggregateReportPath)}"));
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
#endif
}

#if WINDOWS
public sealed class OnnxModelBenchmarkRunnerTests
{
    internal static string SampleModelPath =>
        Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "models", "silero-vad", "onnx", "model.onnx"));

    private static string WhisperEncoderModelPath =>
        Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "models", "whisper-tiny-onnx", "onnx", "encoder_model.onnx"));

    private static string OpusEncoderModelPath =>
        Path.GetFullPath(Path.Join(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "models", "opus", "Helsinki-NLP-opus-mt-en-es", "encoder_model.onnx"));

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_BuildsPlannedReportForExistingModel()
    {
        var modelPath = SampleModelPath;
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(modelPath, reportPath, BenchmarkProviderPreference.Cpu, 3),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.Equal(3, report.RunCount);
            Assert.True(report.ModelSizeBytes > 0);
            Assert.NotNull(report.Measurements.ColdLoadMilliseconds);
            Assert.NotNull(report.Measurements.WarmupMilliseconds);
            Assert.NotNull(report.Measurements.WarmLatencyAverageMilliseconds);
            Assert.NotNull(report.Measurements.WarmLatencyMinimumMilliseconds);
            Assert.NotNull(report.Measurements.WarmLatencyMaximumMilliseconds);
            Assert.NotNull(report.Measurements.AudioDurationSeconds);
            Assert.NotNull(report.Measurements.RealTimeFactorAverage);
            Assert.True(report.Measurements.AudioDurationSeconds > 0);
            Assert.True(report.Measurements.RealTimeFactorAverage > 0);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForManifestScopedSileroModel()
    {
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest("silero-vad", reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.EndsWith(Path.Join("models", "silero-vad", "onnx", "model.onnx"), report.ModelPath, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(report.Notes, note => note.Contains("using manifest", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("whisper-tiny-onnx/onnx/encoder_model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForWhisperEncoderModel()
    {
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(WhisperEncoderModelPath, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.Equal("whisper-encoder-decoder", report.Scenario);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.NotNull(report.Measurements.AudioDurationSeconds);
            Assert.NotNull(report.Measurements.RealTimeFactorAverage);
            Assert.True(report.Measurements.AudioDurationSeconds > 0);
            Assert.True(report.Measurements.RealTimeFactorAverage > 0);
            Assert.Contains(report.Notes, note => note.Contains("Whisper decoder discovered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("opus/Helsinki-NLP-opus-mt-en-es/encoder_model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForOpusEncoderModel()
    {
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(OpusEncoderModelPath, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.Equal("opus-mt-encoder-decoder", report.Scenario);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.Null(report.Measurements.AudioDurationSeconds);
            Assert.Null(report.Measurements.RealTimeFactorAverage);
            Assert.Contains(report.Notes, note => note.Contains("Opus decoder discovered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("opus/Helsinki-NLP-opus-mt-en-es/encoder_model.onnx")]
    public async Task RunAsync_BuildsCompletedReportForManifestScopedOpusModel()
    {
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest("opus-en-es", reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.Equal("opus-mt-encoder-decoder", report.Scenario);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.EndsWith(
                Path.Join("models", "opus", "Helsinki-NLP-opus-mt-en-es", "encoder_model.onnx"),
                report.ModelPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(report.Notes, note => note.Contains("using manifest", StringComparison.Ordinal));
            Assert.Contains(report.Notes, note => note.Contains("Opus decoder discovered", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_ResolvesSingleOnnxFileFromOnnxSubdirectory()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Join(tempDirectory, "onnx");
        string copiedModelPath = Path.Join(onnxDirectory, "variant_fp16.onnx");
        string reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Directory.CreateDirectory(onnxDirectory);
        File.Copy(SampleModelPath, copiedModelPath);

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(tempDirectory, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Completed, report.Status);
            Assert.True(report.SupportsExecution);
            Assert.Equal("cpu", report.SelectedProvider);
            Assert.Equal(copiedModelPath, report.ModelPath);
            Assert.Contains(report.Notes, note => note.Contains("Resolved model directory", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [RequiresBundledModelTheory("silero-vad/onnx/model.onnx")]
    [InlineData(BenchmarkProviderPreference.Cpu, "cpu", "cpu")]
    [InlineData(BenchmarkProviderPreference.Dml, "dml", "dml")]
    [InlineData(BenchmarkProviderPreference.Auto, "auto", "auto")]
    public async Task RunAsync_UsesExplicitProviderRouting(
        BenchmarkProviderPreference preference,
        string expectedRequestedProvider,
        string expectedSelectedProvider)
    {
        var modelPath = SampleModelPath;
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            BenchmarkReport report;

            try
            {
                report = await runner.RunAsync(
                    new BenchmarkRequest(modelPath, reportPath, preference, 1),
                    TestContext.Current.CancellationToken);
            }
            catch (Exception ex) when (
                preference is BenchmarkProviderPreference.Dml &&
                ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
            {
                return;
            }

            Assert.Equal(expectedRequestedProvider, report.RequestedProvider);
            if (preference is BenchmarkProviderPreference.Auto)
            {
                Assert.True(report.SelectedProvider is "cpu" or "dml");
            }
            else
            {
                Assert.Equal(expectedSelectedProvider, report.SelectedProvider);
            }
            Assert.Contains(report.Notes, note => note.Contains("Provider route:", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [RequiresBundledModelFact("silero-vad/onnx/model.onnx")]
    public async Task RunAsync_RecordsWindowsMlBootstrapOutcomeForNonCpuProviders()
    {
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(SampleModelPath, reportPath, BenchmarkProviderPreference.Auto, 1),
                TestContext.Current.CancellationToken);

            Assert.Contains(
                report.Notes,
                note => note.Contains("Windows ML bootstrap", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(reportPath);
        }
    }
}
#endif

public sealed class BenchmarkReportWriterTests
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    [Fact]
    public async Task WriteAsync_WritesJsonReport()
    {
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        var report = new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: "model.onnx",
            ReportPath: reportPath,
            Status: BenchmarkStatus.Completed,
            RequestedProvider: "cpu",
            SelectedProvider: "cpu",
            RunCount: 5,
            SupportsExecution: true,
            ModelSizeBytes: 42,
            Measurements: new BenchmarkMeasurements(10, 5, 2, 1, 3, 0.032, 0.0625),
            FailureReason: null,
            Notes: ["slice wired"],
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        try
        {
            await BenchmarkReportWriter.WriteAsync(report, ReportFormat.Json, TestContext.Current.CancellationToken);

            await using var stream = File.OpenRead(reportPath);
            var written = await JsonSerializer.DeserializeAsync<BenchmarkReport>(stream, SerializerOptions);

            Assert.NotNull(written);
            Assert.Equal(report.Scenario, written.Scenario);
            Assert.Equal(report.ModelSizeBytes, written.ModelSizeBytes);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }
}

public sealed class BenchmarkConsoleTests
{
    [Fact]
    public void WriteSummary_IncludesRequestedAndSelectedProvider()
    {
        var report = new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: "model.onnx",
            ReportPath: "report.json",
            Status: BenchmarkStatus.Planned,
            RequestedProvider: "auto",
            SelectedProvider: "cpu",
            RunCount: 5,
            SupportsExecution: true,
            ModelSizeBytes: 42,
            Measurements: new BenchmarkMeasurements(10, 5, 2, 1, 3, 0.032, 0.0625),
            FailureReason: null,
            Notes: [],
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        using var writer = new StringWriter();

        BenchmarkConsole.WriteSummary(report, writer);

        var summary = writer.ToString();
        Assert.Contains("Requested provider: auto", summary, StringComparison.Ordinal);
        Assert.Contains("Selected provider: cpu", summary, StringComparison.Ordinal);
        Assert.Contains("Cold load: 10.00 ms", summary, StringComparison.Ordinal);
        Assert.Contains("Warm latency avg/min/max: 2.00 ms / 1.00 ms / 3.00 ms", summary, StringComparison.Ordinal);
        Assert.Contains("Audio duration: 0.032 s", summary, StringComparison.Ordinal);
        Assert.Contains("Real-time factor: 0.062x", summary, StringComparison.Ordinal);
    }
}

public sealed class BenchmarkFailureTests
{
#if WINDOWS
    [Fact]
    public async Task RunAsync_ReturnsFailedReportForMissingModel()
    {
        var modelPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.onnx");
        var reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(modelPath, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Failed, report.Status);
            Assert.False(report.SupportsExecution);
            Assert.NotNull(report.FailureReason);
            Assert.Contains("did not resolve", report.FailureReason, StringComparison.Ordinal);
            Assert.Null(report.Measurements.ColdLoadMilliseconds);
        }
        finally
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task RunAsync_ReturnsFailedReportForAmbiguousVariantDirectory()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), $"trackdub-bench-{Guid.NewGuid():N}");
        string onnxDirectory = Path.Join(tempDirectory, "onnx");
        string reportPath = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Directory.CreateDirectory(onnxDirectory);
        File.WriteAllBytes(Path.Join(onnxDirectory, "variant_a.onnx"), []);
        File.WriteAllBytes(Path.Join(onnxDirectory, "variant_b.onnx"), []);

        try
        {
            var runner = new OnnxModelBenchmarkRunner();
            var report = await runner.RunAsync(
                new BenchmarkRequest(tempDirectory, reportPath, BenchmarkProviderPreference.Cpu, 1),
                TestContext.Current.CancellationToken);

            Assert.Equal(BenchmarkStatus.Failed, report.Status);
            Assert.False(report.SupportsExecution);
            Assert.NotNull(report.FailureReason);
            Assert.Contains("ambiguous", report.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("variant_a", report.FailureReason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("variant_b", report.FailureReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(reportPath);
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }
#endif

    [Fact]
    public void WriteSummary_IncludesFailureReason()
    {
        var report = new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: "missing.onnx",
            ReportPath: "report.json",
            Status: BenchmarkStatus.Failed,
            RequestedProvider: "cpu",
            SelectedProvider: "cpu",
            RunCount: 1,
            SupportsExecution: false,
            ModelSizeBytes: 0,
            Measurements: new BenchmarkMeasurements(null, null, null, null, null, null, null),
            FailureReason: "Model path does not exist.",
            Notes: ["Benchmark failed: FileNotFoundException"],
            GeneratedAtUtc: DateTimeOffset.UtcNow);

        using var writer = new StringWriter();
        BenchmarkConsole.WriteSummary(report, writer);
        var summary = writer.ToString();

        Assert.Contains("Failure reason: Model path does not exist.", summary, StringComparison.Ordinal);
    }
}

public sealed class BundledModelManifestRegistryTests
{
    [Fact]
    public void TryLoadDefault_ResolvesSileroAlias()
    {
        var success = BundledModelManifestRegistry.TryLoadDefault(out var registry, out var error);

        Assert.True(success, error);
        Assert.NotNull(registry);
        Assert.True(registry!.TryResolve("silero-vad", out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal("silero-vad", resolution!.Alias);
        Assert.EndsWith(Path.Join("models", "silero-vad", "onnx", "model.onnx"), resolution.EntryPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("MIT", resolution.Entry.License);
    }

    [Fact]
    public void TryLoadDefault_ResolvesNamedVariant()
    {
        var success = BundledModelManifestRegistry.TryLoadDefault(out var registry, out var error);

        Assert.True(success, error);
        Assert.NotNull(registry);
        Assert.True(registry!.TryResolve("silero-vad@q4", out var resolution));
        Assert.NotNull(resolution);
        Assert.Equal("q4", resolution!.VariantAlias);
        Assert.EndsWith(Path.Join("models", "silero-vad", "onnx", "model_q4.onnx"), resolution.EntryPath, StringComparison.OrdinalIgnoreCase);
    }

#if WINDOWS
    [WindowsOnlyFact]
    public void CreateOnnxRunner_ReturnsRunnerOnWindowsTarget()
    {
        IModelBenchmarkRunner? runner = BenchmarkOnnxExecutionBootstrap.CreateOnnxRunner();

        Assert.NotNull(runner);
    }
#endif
}
