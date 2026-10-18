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
        out BenchmarkOptions options) =>
        BenchmarkOptionsParser.TryParse(args, errorWriter, out options);

    /// <summary>
    /// Maps the parsed flags onto <see cref="BenchmarkExecutionSettings"/>. Without an explicit
    /// --runs the measurement floor defaults to 3 seconds on top of the 10-sample minimum;
    /// --duration-ms without --runs keeps that sample minimum and applies the duration floor;
    /// an explicit --runs without --duration-ms preserves exact-count runs.
    /// </summary>
    internal static BenchmarkExecutionSettings BuildExecutionSettings(
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

    internal static bool TryParseInputShape(string value, out string name, out int[] dimensions)
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
    internal static bool TryParseFlagEnum<TEnum>(string value, out TEnum result)
        where TEnum : struct, Enum =>
        Enum.TryParse(value.Replace("-", string.Empty).Replace("_", string.Empty), ignoreCase: true, out result)
            && Enum.IsDefined(result);

    internal static bool TryParseReferenceTool(string value, out BenchmarkReferenceToolKind kind)
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

    internal static BenchmarkOptions DefaultWithHelp() =>
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

    internal static bool TryParseProviderPreference(string value, out BenchmarkProviderPreference preference)
    {
        if (string.Equals(value, "trt-rtx", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "tensorrt-rtx", StringComparison.OrdinalIgnoreCase))
        {
            preference = BenchmarkProviderPreference.TensorRtRtx;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out preference);
    }

    internal static bool TryReadValue(
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
