using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

public sealed record BenchmarkOptions(
    string ModelPath,
    string OutputPath,
    BenchmarkProviderPreference ProviderPreference,
    int RunCount,
    string? Variant,
    bool AllVariants,
    ReportFormat ReportFormat,
    string? WindowsMlDevicePolicyKey,
    string? Scope,
    bool ShowHelp,
    bool Mock = false,
    bool DryRun = false)
{
    /// <summary>Sampling/input/I-O policy for the measured run; CLI always supplies it.</summary>
    public BenchmarkExecutionSettings? Execution { get; init; }

    /// <summary>Opt-in persistence of this run into the user benchmark history store.</summary>
    public bool HistoryRequested { get; init; }

    /// <summary>CUDA-graph lane requested on the command line; Auto resolves from device-bound eligibility.</summary>
    public BenchmarkCudaGraphMode CudaGraphMode { get; init; } = BenchmarkCudaGraphMode.Auto;

    /// <summary>Optional NVIDIA reference-tool run attached to the primary benchmark.</summary>
    public BenchmarkReferenceToolKind ReferenceTool { get; init; } = BenchmarkReferenceToolKind.None;

    public string? ReferenceToolPath { get; init; }

    /// <summary>When true an unsatisfied reference tool fails the command (exit code 2).</summary>
    public bool ReferenceToolRequired { get; init; }

    public static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter errorWriter,
        out BenchmarkOptions options)
    {
        string? modelPath = null;
        var outputPath = Path.Join(Environment.CurrentDirectory, "benchmark-report.json");
        var providerPreference = BenchmarkProviderPreference.Cpu;
        var runCount = 5;
        var runCountSpecified = false;
        int? durationMilliseconds = null;
        var warmupRuns = 1;
        var warmupMilliseconds = 200;
        var inputMode = BenchmarkInputMode.Synthetic;
        var inputSeed = 42;
        var inputShapeOverrides = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var ioMode = BenchmarkIoMode.Managed;
        var artifactMode = BenchmarkArtifactMode.Auto;
        var runtimeCacheMode = BenchmarkRuntimeCacheMode.Reuse;
        string? runtimeCacheDirectory = null;
        var cudaGraphMode = BenchmarkCudaGraphMode.Auto;
        var referenceTool = BenchmarkReferenceToolKind.None;
        string? referenceToolPath = null;
        var referenceToolRequired = false;
        var historyRequested = false;
        string? variant = null;
        var allVariants = false;
        var reportFormat = ReportFormat.Both;
        string? windowsMlDevicePolicyKey = null;
        string? scope = null;
        var showHelp = false;
        var mock = false;
        var dryRun = false;

        for (var index = 0; index < args.Count; index++)
        {
            string arg = args[index];

            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                case "--mock":
                case "--dry-run":
                    errorWriter.WriteLine($"{arg} is only supported by the controlled, controlled-matrix, and matrix commands.");
                    options = DefaultWithHelp();
                    return false;

                case "--model":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out modelPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--scope":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out scope))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    scope = scope.Trim();
                    break;

                case "--output":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out outputPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--provider":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string providerText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    if (!TryParseProviderPreference(providerText, out providerPreference))
                    {
                        errorWriter.WriteLine($"Unknown provider '{providerText}'. Expected auto, cpu, dml, cuda, tensorrt, migraphx, qnn, or trt-rtx.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--runs":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string runCountText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    if (!int.TryParse(runCountText, out runCount) || runCount <= 0)
                    {
                        errorWriter.WriteLine($"Invalid run count '{runCountText}'.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    runCountSpecified = true;
                    break;

                case "--duration-ms":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string durationText)
                        || !int.TryParse(durationText, out int durationValue)
                        || durationValue < 0)
                    {
                        errorWriter.WriteLine($"Invalid measurement duration '{durationText}'. Expected a non-negative millisecond count.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    durationMilliseconds = durationValue;
                    break;

                case "--warmup-runs":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string warmupRunsText)
                        || !int.TryParse(warmupRunsText, out warmupRuns)
                        || warmupRuns < 1)
                    {
                        errorWriter.WriteLine($"Invalid warmup run count '{warmupRunsText}'. Expected a positive count.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--warmup-ms":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string warmupMsText)
                        || !int.TryParse(warmupMsText, out warmupMilliseconds)
                        || warmupMilliseconds < 0)
                    {
                        errorWriter.WriteLine($"Invalid warmup duration '{warmupMsText}'. Expected a non-negative millisecond count.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--input-mode":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string inputModeText)
                        || !TryParseFlagEnum(inputModeText, out inputMode))
                    {
                        errorWriter.WriteLine($"Unknown input mode '{inputModeText}'. Expected synthetic, zeros, or profile.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--input-seed":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string inputSeedText)
                        || !int.TryParse(inputSeedText, out inputSeed))
                    {
                        errorWriter.WriteLine($"Invalid input seed '{inputSeedText}'.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--input-shape":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string inputShapeText)
                        || !TryParseInputShape(inputShapeText, out string shapeName, out int[] shapeDims))
                    {
                        errorWriter.WriteLine(
                            $"Invalid input shape override '{inputShapeText}'. Expected <name>=<d1,d2,...> with dimensions >= 1.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    inputShapeOverrides[shapeName] = shapeDims;
                    break;

                case "--io-mode":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string ioModeText)
                        || !TryParseFlagEnum(ioModeText, out ioMode))
                    {
                        errorWriter.WriteLine($"Unknown I/O mode '{ioModeText}'. Expected managed or device-bound.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--cuda-graphs":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string cudaGraphText)
                        || !TryParseFlagEnum(cudaGraphText, out cudaGraphMode))
                    {
                        errorWriter.WriteLine($"Unknown CUDA graph mode '{cudaGraphText}'. Expected off, on, or compare.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--artifact":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string artifactText)
                        || !TryParseFlagEnum(artifactText, out artifactMode))
                    {
                        errorWriter.WriteLine($"Unknown artifact mode '{artifactText}'. Expected auto, source, or require.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--runtime-cache":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string cacheModeText)
                        || !TryParseFlagEnum(cacheModeText, out runtimeCacheMode))
                    {
                        errorWriter.WriteLine($"Unknown runtime cache mode '{cacheModeText}'. Expected reuse or isolated.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--runtime-cache-dir":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string cacheDirText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    runtimeCacheDirectory = cacheDirText.Trim();
                    break;

                case "--history":
                    historyRequested = true;
                    break;

                case "--reference-tool":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string referenceToolText)
                        || !TryParseReferenceTool(referenceToolText, out referenceTool))
                    {
                        errorWriter.WriteLine($"Unknown reference tool '{referenceToolText}'. Expected tensorrt-rtx or ort-perf-test.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--reference-tool-path":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string referenceToolPathText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    referenceToolPath = referenceToolPathText.Trim();
                    break;

                case "--reference-tool-required":
                    referenceToolRequired = true;
                    break;

                case "--variant":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out variant))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    variant = variant.Trim();
                    break;

                case "--all-variants":
                    allVariants = true;
                    break;

                case "--windows-ml-device-policy":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string policyText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    string normalizedPolicy = policyText.Trim();
                    if (string.IsNullOrEmpty(normalizedPolicy))
                    {
                        errorWriter.WriteLine("--windows-ml-device-policy requires a non-empty value.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    if (!WindowsMlExecutionDevicePolicySettings.TryParseKey(normalizedPolicy, out _))
                    {
                        errorWriter.WriteLine(
                            $"Unknown Windows ML device policy '{normalizedPolicy}'. " +
                            $"Expected one of: {WindowsMlExecutionDevicePolicySettings.ExplicitKey}, " +
                            $"{WindowsMlExecutionDevicePolicySettings.MaxPerformanceKey}, " +
                            $"{WindowsMlExecutionDevicePolicySettings.PreferNpuKey}, " +
                            $"{WindowsMlExecutionDevicePolicySettings.MaxEfficiencyKey}, " +
                            $"{WindowsMlExecutionDevicePolicySettings.MinOverallPowerKey}.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    windowsMlDevicePolicyKey = normalizedPolicy;
                    break;

                case "--format":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out string formatText))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    if (!Enum.TryParse(formatText, ignoreCase: true, out reportFormat))
                    {
                        errorWriter.WriteLine($"Unknown format '{formatText}'. Expected console, json, or both.");
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                default:
                    errorWriter.WriteLine($"Unknown argument '{arg}'.");
                    options = DefaultWithHelp();
                    return false;
            }
        }

        // Sampling floors come from the shared domain policy so CLI and other callers
        // cannot drift: absent --runs means >=10 samples plus a 3s measurement floor;
        // explicit --runs keeps exact-count behavior (including --runs 1 smoke runs).
        BenchmarkSamplingPolicy sampling = BenchmarkSamplingPolicy.Resolve(
            runCountSpecified ? runCount : null,
            durationMilliseconds);

        if (showHelp)
        {
            options = new BenchmarkOptions(
                string.Empty,
                outputPath,
                providerPreference,
                runCount,
                variant,
                allVariants,
                reportFormat,
                null,
                scope,
                ShowHelp: true)
            {
                Execution = BuildExecutionSettings(
                    sampling, warmupRuns, warmupMilliseconds,
                    inputMode, inputSeed, ioMode, artifactMode, runtimeCacheMode,
                    runtimeCacheDirectory, cudaGraphMode, inputShapeOverrides)
            };
            return true;
        }

        if (!string.IsNullOrWhiteSpace(scope) &&
            !scope.Equals(TrtRtxSmokeCatalog.ScopeName, StringComparison.OrdinalIgnoreCase))
        {
            errorWriter.WriteLine($"Unknown scope '{scope}'. Expected {TrtRtxSmokeCatalog.ScopeName}.");
            options = DefaultWithHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(modelPath) && string.IsNullOrWhiteSpace(scope))
        {
            errorWriter.WriteLine("Missing required argument --model <path-or-scope> or --scope trt-rtx-smoke.");
            options = DefaultWithHelp();
            return false;
        }

        if (!string.IsNullOrWhiteSpace(modelPath) && !string.IsNullOrWhiteSpace(scope))
        {
            errorWriter.WriteLine("Cannot combine --model with --scope.");
            options = DefaultWithHelp();
            return false;
        }

        if (allVariants && !string.IsNullOrWhiteSpace(variant))
        {
            errorWriter.WriteLine("Cannot combine --variant with --all-variants.");
            options = DefaultWithHelp();
            return false;
        }

        if (referenceToolRequired && referenceTool is BenchmarkReferenceToolKind.None)
        {
            errorWriter.WriteLine("--reference-tool-required requires --reference-tool.");
            options = DefaultWithHelp();
            return false;
        }

        int effectiveRunCount = sampling.MinimumSamples;

        options = new BenchmarkOptions(
            string.IsNullOrWhiteSpace(modelPath) ? string.Empty : modelPath.Trim(),
            Path.GetFullPath(outputPath),
            providerPreference,
            effectiveRunCount,
            string.IsNullOrWhiteSpace(variant) ? null : variant,
            allVariants,
            reportFormat,
            windowsMlDevicePolicyKey,
            string.IsNullOrWhiteSpace(scope) ? null : scope,
            ShowHelp: false,
            Mock: mock,
            DryRun: dryRun)
        {
            Execution = BuildExecutionSettings(
                sampling, warmupRuns, warmupMilliseconds,
                inputMode, inputSeed, ioMode, artifactMode, runtimeCacheMode,
                runtimeCacheDirectory, cudaGraphMode, inputShapeOverrides),
            HistoryRequested = historyRequested,
            CudaGraphMode = cudaGraphMode,
            ReferenceTool = referenceTool,
            ReferenceToolPath = string.IsNullOrWhiteSpace(referenceToolPath) ? null : referenceToolPath,
            ReferenceToolRequired = referenceToolRequired
        };

        return true;
    }

    /// <summary>
    /// Maps the parsed flags onto <see cref="BenchmarkExecutionSettings"/>. Without an explicit
    /// --runs the measurement floor defaults to 3 seconds on top of the 10-sample minimum;
    /// --duration-ms without --runs keeps that sample minimum and applies the duration floor;
    /// an explicit --runs without --duration-ms preserves exact-count runs.
    /// </summary>
    private static BenchmarkExecutionSettings BuildExecutionSettings(
        BenchmarkSamplingPolicy sampling,
        int warmupRuns,
        int warmupMilliseconds,
        BenchmarkInputMode inputMode,
        int inputSeed,
        BenchmarkIoMode ioMode,
        BenchmarkArtifactMode artifactMode,
        BenchmarkRuntimeCacheMode runtimeCacheMode,
        string? runtimeCacheDirectory,
        BenchmarkCudaGraphMode cudaGraphMode,
        IReadOnlyDictionary<string, int[]> inputShapeOverrides) =>
        new(
            MinimumWarmupRuns: warmupRuns,
            MinimumWarmupMilliseconds: warmupMilliseconds,
            MinimumMeasurementMilliseconds: sampling.MinimumMeasurementMilliseconds,
            InputMode: inputMode,
            InputSeed: inputSeed,
            IoMode: ioMode,
            TensorRtRtx: new BenchmarkTensorRtRtxSettings(
                CudaGraphMode: cudaGraphMode,
                ArtifactMode: artifactMode,
                CacheMode: runtimeCacheMode,
                RuntimeCacheDirectory: runtimeCacheDirectory),
            InputShapeOverrides: inputShapeOverrides.Count == 0 ? null : inputShapeOverrides);

    private static bool TryParseInputShape(string value, out string name, out int[] dimensions)
    {
        name = string.Empty;
        dimensions = [];
        var separatorIndex = value.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        name = value[..separatorIndex].Trim();
        if (name.Length == 0)
        {
            return false;
        }

        var parts = value[(separatorIndex + 1)..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0
            || !parts.All(p => int.TryParse(p, out _)))
        {
            return false;
        }

        dimensions = parts.Select(int.Parse).ToArray();
        return dimensions.All(d => d >= 1);
    }

    /// <summary>Parses kebab/underscore CLI values like "device-bound" onto enum members.</summary>
    private static bool TryParseFlagEnum<TEnum>(string value, out TEnum result)
        where TEnum : struct, Enum =>
        Enum.TryParse(value.Replace("-", string.Empty).Replace("_", string.Empty), ignoreCase: true, out result)
            && Enum.IsDefined(result);

    private static bool TryParseReferenceTool(string value, out BenchmarkReferenceToolKind kind)
    {
        if (string.Equals(value, "tensorrt-rtx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "tensorrt_rtx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "trt-rtx", StringComparison.OrdinalIgnoreCase))
        {
            kind = BenchmarkReferenceToolKind.TensorRtRtx;
            return true;
        }

        if (string.Equals(value, "ort-perf-test", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "onnxruntime_perf_test", StringComparison.OrdinalIgnoreCase))
        {
            kind = BenchmarkReferenceToolKind.OrtPerfTest;
            return true;
        }

        kind = BenchmarkReferenceToolKind.None;
        return false;
    }

    private static BenchmarkOptions DefaultWithHelp() =>
        new(
            string.Empty,
            Path.Join(Environment.CurrentDirectory, "benchmark-report.json"),
            BenchmarkProviderPreference.Cpu,
            5,
            null,
            false,
            ReportFormat.Both,
            null,
            null,
            ShowHelp: true,
            Mock: false,
            DryRun: false);

    private static bool TryParseProviderPreference(string value, out BenchmarkProviderPreference preference)
    {
        if (string.Equals(value, "trt-rtx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "tensorrt-rtx", StringComparison.OrdinalIgnoreCase))
        {
            preference = BenchmarkProviderPreference.TensorRtRtx;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out preference);
    }

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        TextWriter errorWriter,
        out string value)
    {
        if (index + 1 >= args.Count)
        {
            errorWriter.WriteLine($"Missing value for {optionName}.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }
}

public enum ReportFormat
{
    Console,
    Json,
    Both
}
