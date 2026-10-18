using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

internal static class BenchmarkOptionsParser
{
    private enum ParseResult
    {
        NotMatched,
        Parsed,
        Failed
    }

    private sealed class State
    {
        internal string? ModelPath { get; set; }
        internal string OutputPath { get; set; } = Path.Join(Environment.CurrentDirectory, "benchmark-report.json");
        internal BenchmarkProviderPreference ProviderPreference { get; set; } = BenchmarkProviderPreference.Cpu;
        internal int RunCount { get; set; } = 5;
        internal bool RunCountSpecified { get; set; }
        internal int? DurationMilliseconds { get; set; }
        internal int WarmupRuns { get; set; } = 1;
        internal int WarmupMilliseconds { get; set; } = 200;
        internal BenchmarkInputMode InputMode { get; set; } = BenchmarkInputMode.Synthetic;
        internal int InputSeed { get; set; } = 42;
        internal Dictionary<string, int[]> InputShapeOverrides { get; } = new(StringComparer.Ordinal);
        internal BenchmarkIoMode IoMode { get; set; } = BenchmarkIoMode.Managed;
        internal BenchmarkArtifactMode ArtifactMode { get; set; } = BenchmarkArtifactMode.Auto;
        internal BenchmarkRuntimeCacheMode RuntimeCacheMode { get; set; } = BenchmarkRuntimeCacheMode.Reuse;
        internal string? RuntimeCacheDirectory { get; set; }
        internal BenchmarkCudaGraphMode CudaGraphMode { get; set; } = BenchmarkCudaGraphMode.Auto;
        internal BenchmarkReferenceToolKind ReferenceTool { get; set; } = BenchmarkReferenceToolKind.None;
        internal string? ReferenceToolPath { get; set; }
        internal bool ReferenceToolRequired { get; set; }
        internal bool HistoryRequested { get; set; }
        internal string? Variant { get; set; }
        internal bool AllVariants { get; set; }
        internal ReportFormat ReportFormat { get; set; } = ReportFormat.Both;
        internal string? WindowsMlDevicePolicyKey { get; set; }
        internal string? Scope { get; set; }
        internal bool ShowHelp { get; set; }
    }

    internal static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter errorWriter,
        out BenchmarkOptions options)
    {
        var state = new State();
        for (int index = 0; index < args.Count; index++)
        {
            ParseResult result = TryParseArgument(args, ref index, state, errorWriter);
            if (result is ParseResult.Failed)
            {
                options = BenchmarkOptions.DefaultWithHelp();
                return false;
            }

            if (result is ParseResult.NotMatched)
            {
                errorWriter.WriteLine($"Unknown argument '{args[index]}'.");
                options = BenchmarkOptions.DefaultWithHelp();
                return false;
            }
        }

        BenchmarkSamplingPolicy sampling = BenchmarkSamplingPolicy.Resolve(
            state.RunCountSpecified ? state.RunCount : null,
            state.DurationMilliseconds);
        if (state.ShowHelp)
        {
            options = new BenchmarkOptions(
                string.Empty,
                state.OutputPath,
                state.ProviderPreference,
                state.RunCount,
                state.Variant,
                state.AllVariants,
                state.ReportFormat,
                null,
                state.Scope,
                ShowHelp: true)
            {
                Execution = BenchmarkOptions.BuildExecutionSettings(
                    sampling,
                    state.WarmupRuns,
                    state.WarmupMilliseconds,
                    state.InputMode,
                    state.InputSeed,
                    state.IoMode,
                    state.ArtifactMode,
                    state.RuntimeCacheMode,
                    state.RuntimeCacheDirectory,
                    state.CudaGraphMode,
                    state.InputShapeOverrides)
            };
            return true;
        }

        if (!ValidateFinalOptions(state, errorWriter))
        {
            options = BenchmarkOptions.DefaultWithHelp();
            return false;
        }

        options = new BenchmarkOptions(
            string.IsNullOrWhiteSpace(state.ModelPath) ? string.Empty : state.ModelPath.Trim(),
            Path.GetFullPath(state.OutputPath),
            state.ProviderPreference,
            sampling.MinimumSamples,
            string.IsNullOrWhiteSpace(state.Variant) ? null : state.Variant,
            state.AllVariants,
            state.ReportFormat,
            state.WindowsMlDevicePolicyKey,
            string.IsNullOrWhiteSpace(state.Scope) ? null : state.Scope,
            ShowHelp: false)
        {
            Execution = BenchmarkOptions.BuildExecutionSettings(
                sampling,
                state.WarmupRuns,
                state.WarmupMilliseconds,
                state.InputMode,
                state.InputSeed,
                state.IoMode,
                state.ArtifactMode,
                state.RuntimeCacheMode,
                state.RuntimeCacheDirectory,
                state.CudaGraphMode,
                state.InputShapeOverrides),
            HistoryRequested = state.HistoryRequested,
            CudaGraphMode = state.CudaGraphMode,
            ReferenceTool = state.ReferenceTool,
            ReferenceToolPath = string.IsNullOrWhiteSpace(state.ReferenceToolPath) ? null : state.ReferenceToolPath,
            ReferenceToolRequired = state.ReferenceToolRequired
        };
        return true;
    }

    private static ParseResult TryParseArgument(
        IReadOnlyList<string> args,
        ref int index,
        State state,
        TextWriter errorWriter)
    {
        string argument = args[index];
        ParseResult result = TryParseFlags(argument, state, errorWriter);
        if (result is not ParseResult.NotMatched) return result;
        result = TryParseSamplingOption(args, ref index, argument, state, errorWriter);
        if (result is not ParseResult.NotMatched) return result;
        result = TryParseInputOption(args, ref index, argument, state, errorWriter);
        if (result is not ParseResult.NotMatched) return result;
        result = TryParseRuntimeOption(args, ref index, argument, state, errorWriter);
        if (result is not ParseResult.NotMatched) return result;
        result = TryParseReferenceToolOption(args, ref index, argument, state, errorWriter);
        if (result is not ParseResult.NotMatched) return result;
        result = TryParseModelOption(args, ref index, argument, state, errorWriter);
        return result;
    }

    private static ParseResult TryParseFlags(string argument, State state, TextWriter errorWriter) => argument switch
    {
        "--help" or "-h" or "/?" => SetHelp(state),
        "--history" => SetHistory(state),
        "--all-variants" => SetAllVariants(state),
        "--reference-tool-required" => SetReferenceToolRequired(state),
        "--mock" or "--dry-run" => Fail(
            errorWriter,
            $"{argument} is only supported by the controlled, controlled-matrix, and matrix commands."),
        _ => ParseResult.NotMatched
    };

    private static ParseResult TryParseSamplingOption(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        State state,
        TextWriter errorWriter)
    {
        switch (argument)
        {
            case "--runs":
                if (!ReadValue(args, ref index, argument, errorWriter, out string runCountText)) return ParseResult.Failed;
                if (!int.TryParse(runCountText, out int runCount) || runCount <= 0)
                    return Fail(errorWriter, $"Invalid run count '{runCountText}'.");
                state.RunCount = runCount;
                state.RunCountSpecified = true;
                return ParseResult.Parsed;

            case "--duration-ms":
                if (!ReadValue(args, ref index, argument, errorWriter, out string durationText)
                    || !int.TryParse(durationText, out int duration) || duration < 0)
                    return Fail(errorWriter, $"Invalid measurement duration '{durationText}'. Expected a non-negative millisecond count.");
                state.DurationMilliseconds = duration;
                return ParseResult.Parsed;

            case "--warmup-runs":
                if (!ReadValue(args, ref index, argument, errorWriter, out string warmupRunsText)
                    || !int.TryParse(warmupRunsText, out int warmupRuns) || warmupRuns < 1)
                    return Fail(errorWriter, $"Invalid warmup run count '{warmupRunsText}'. Expected a positive count.");
                state.WarmupRuns = warmupRuns;
                return ParseResult.Parsed;

            case "--warmup-ms":
                if (!ReadValue(args, ref index, argument, errorWriter, out string warmupMillisecondsText)
                    || !int.TryParse(warmupMillisecondsText, out int warmupMilliseconds) || warmupMilliseconds < 0)
                    return Fail(errorWriter, $"Invalid warmup duration '{warmupMillisecondsText}'. Expected a non-negative millisecond count.");
                state.WarmupMilliseconds = warmupMilliseconds;
                return ParseResult.Parsed;

            default:
                return ParseResult.NotMatched;
        }
    }

    private static ParseResult TryParseInputOption(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        State state,
        TextWriter errorWriter)
    {
        switch (argument)
        {
            case "--input-mode":
                if (!ReadValue(args, ref index, argument, errorWriter, out string inputModeText)
                    || !BenchmarkOptions.TryParseFlagEnum(inputModeText, out BenchmarkInputMode inputMode))
                    return Fail(errorWriter, $"Unknown input mode '{inputModeText}'. Expected synthetic, zeros, or profile.");
                state.InputMode = inputMode;
                return ParseResult.Parsed;

            case "--input-seed":
                if (!ReadValue(args, ref index, argument, errorWriter, out string inputSeedText)
                    || !int.TryParse(inputSeedText, out int inputSeed))
                    return Fail(errorWriter, $"Invalid input seed '{inputSeedText}'.");
                state.InputSeed = inputSeed;
                return ParseResult.Parsed;

            case "--input-shape":
                if (!ReadValue(args, ref index, argument, errorWriter, out string inputShapeText)
                    || !BenchmarkOptions.TryParseInputShape(inputShapeText, out string name, out int[] dimensions))
                    return Fail(errorWriter,
                        $"Invalid input shape override '{inputShapeText}'. Expected <name>=<d1,d2,...> with dimensions >= 1.");
                state.InputShapeOverrides[name] = dimensions;
                return ParseResult.Parsed;

            default:
                return ParseResult.NotMatched;
        }
    }

    private static ParseResult TryParseRuntimeOption(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        State state,
        TextWriter errorWriter)
    {
        switch (argument)
        {
            case "--io-mode":
                if (!ReadValue(args, ref index, argument, errorWriter, out string ioModeText)
                    || !BenchmarkOptions.TryParseFlagEnum(ioModeText, out BenchmarkIoMode ioMode))
                    return Fail(errorWriter, $"Unknown I/O mode '{ioModeText}'. Expected managed or device-bound.");
                state.IoMode = ioMode;
                return ParseResult.Parsed;

            case "--cuda-graphs":
                if (!ReadValue(args, ref index, argument, errorWriter, out string cudaGraphText)
                    || !BenchmarkOptions.TryParseFlagEnum(cudaGraphText, out BenchmarkCudaGraphMode cudaGraphMode))
                    return Fail(errorWriter, $"Unknown CUDA graph mode '{cudaGraphText}'. Expected auto, off, on, or compare.");
                state.CudaGraphMode = cudaGraphMode;
                return ParseResult.Parsed;

            case "--artifact":
                if (!ReadValue(args, ref index, argument, errorWriter, out string artifactText)
                    || !BenchmarkOptions.TryParseFlagEnum(artifactText, out BenchmarkArtifactMode artifactMode))
                    return Fail(errorWriter, $"Unknown artifact mode '{artifactText}'. Expected auto, source, or require.");
                state.ArtifactMode = artifactMode;
                return ParseResult.Parsed;

            case "--runtime-cache":
                if (!ReadValue(args, ref index, argument, errorWriter, out string cacheModeText)
                    || !BenchmarkOptions.TryParseFlagEnum(cacheModeText, out BenchmarkRuntimeCacheMode cacheMode))
                    return Fail(errorWriter, $"Unknown runtime cache mode '{cacheModeText}'. Expected reuse or isolated.");
                state.RuntimeCacheMode = cacheMode;
                return ParseResult.Parsed;

            case "--runtime-cache-dir":
                if (!ReadValue(args, ref index, argument, errorWriter, out string cacheDirectory)) return ParseResult.Failed;
                state.RuntimeCacheDirectory = cacheDirectory.Trim();
                return ParseResult.Parsed;

            default:
                return ParseResult.NotMatched;
        }
    }

    private static ParseResult TryParseReferenceToolOption(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        State state,
        TextWriter errorWriter)
    {
        switch (argument)
        {
            case "--reference-tool":
                if (!ReadValue(args, ref index, argument, errorWriter, out string referenceToolText)
                    || !BenchmarkOptions.TryParseReferenceTool(referenceToolText, out BenchmarkReferenceToolKind referenceTool))
                    return Fail(errorWriter, $"Unknown reference tool '{referenceToolText}'. Expected tensorrt-rtx or ort-perf-test.");
                state.ReferenceTool = referenceTool;
                return ParseResult.Parsed;

            case "--reference-tool-path":
                if (!ReadValue(args, ref index, argument, errorWriter, out string referenceToolPath)) return ParseResult.Failed;
                state.ReferenceToolPath = referenceToolPath.Trim();
                return ParseResult.Parsed;

            default:
                return ParseResult.NotMatched;
        }
    }

    private static ParseResult TryParseModelOption(
        IReadOnlyList<string> args,
        ref int index,
        string argument,
        State state,
        TextWriter errorWriter)
    {
        switch (argument)
        {
            case "--model":
                if (!ReadValue(args, ref index, argument, errorWriter, out string modelPath)) return ParseResult.Failed;
                state.ModelPath = modelPath;
                return ParseResult.Parsed;

            case "--output":
                if (!ReadValue(args, ref index, argument, errorWriter, out string outputPath)) return ParseResult.Failed;
                state.OutputPath = outputPath;
                return ParseResult.Parsed;

            case "--scope":
                if (!ReadValue(args, ref index, argument, errorWriter, out string scope)) return ParseResult.Failed;
                state.Scope = scope.Trim();
                return ParseResult.Parsed;

            case "--provider":
                if (!ReadValue(args, ref index, argument, errorWriter, out string providerText)) return ParseResult.Failed;
                if (!BenchmarkOptions.TryParseProviderPreference(providerText, out BenchmarkProviderPreference provider))
                    return Fail(errorWriter, $"Unknown provider '{providerText}'. Expected auto, cpu, dml, cuda, tensorrt, migraphx, qnn, or trt-rtx.");
                state.ProviderPreference = provider;
                return ParseResult.Parsed;

            case "--variant":
                if (!ReadValue(args, ref index, argument, errorWriter, out string variant)) return ParseResult.Failed;
                state.Variant = variant.Trim();
                return ParseResult.Parsed;

            case "--windows-ml-device-policy":
                if (!ReadValue(args, ref index, argument, errorWriter, out string policyText)) return ParseResult.Failed;
                string normalizedPolicy = policyText.Trim();
                if (string.IsNullOrEmpty(normalizedPolicy))
                    return Fail(errorWriter, "--windows-ml-device-policy requires a non-empty value.");
                if (!WindowsMlExecutionDevicePolicySettings.TryParseKey(normalizedPolicy, out _))
                    return Fail(errorWriter,
                        $"Unknown Windows ML device policy '{normalizedPolicy}'. Expected one of: "
                        + $"{WindowsMlExecutionDevicePolicySettings.ExplicitKey}, "
                        + $"{WindowsMlExecutionDevicePolicySettings.MaxPerformanceKey}, "
                        + $"{WindowsMlExecutionDevicePolicySettings.PreferNpuKey}, "
                        + $"{WindowsMlExecutionDevicePolicySettings.MaxEfficiencyKey}, "
                        + $"{WindowsMlExecutionDevicePolicySettings.MinOverallPowerKey}.");
                state.WindowsMlDevicePolicyKey = normalizedPolicy;
                return ParseResult.Parsed;

            case "--format":
                if (!ReadValue(args, ref index, argument, errorWriter, out string formatText)) return ParseResult.Failed;
                if (!Enum.TryParse(formatText, ignoreCase: true, out ReportFormat format))
                    return Fail(errorWriter, $"Unknown format '{formatText}'. Expected console, json, or both.");
                state.ReportFormat = format;
                return ParseResult.Parsed;

            default:
                return ParseResult.NotMatched;
        }
    }

    private static bool ValidateFinalOptions(State state, TextWriter errorWriter)
    {
        if (!string.IsNullOrWhiteSpace(state.Scope)
            && !state.Scope.Equals(TrtRtxSmokeCatalog.ScopeName, StringComparison.OrdinalIgnoreCase))
            return errorWriter.WriteError($"Unknown scope '{state.Scope}'. Expected {TrtRtxSmokeCatalog.ScopeName}.");
        if (string.IsNullOrWhiteSpace(state.ModelPath) && string.IsNullOrWhiteSpace(state.Scope))
            return errorWriter.WriteError("Missing required argument --model <path-or-scope> or --scope trt-rtx-smoke.");
        if (!string.IsNullOrWhiteSpace(state.ModelPath) && !string.IsNullOrWhiteSpace(state.Scope))
            return errorWriter.WriteError("Cannot combine --model with --scope.");
        if (state.AllVariants && !string.IsNullOrWhiteSpace(state.Variant))
            return errorWriter.WriteError("Cannot combine --variant with --all-variants.");
        if (state.ReferenceToolRequired && state.ReferenceTool is BenchmarkReferenceToolKind.None)
            return errorWriter.WriteError("--reference-tool-required requires --reference-tool.");
        return true;
    }

    private static bool ReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string option,
        TextWriter errorWriter,
        out string value) => BenchmarkOptions.TryReadValue(args, ref index, option, errorWriter, out value);

    private static ParseResult Fail(TextWriter errorWriter, string message)
    {
        errorWriter.WriteLine(message);
        return ParseResult.Failed;
    }

    private static ParseResult SetHelp(State state) { state.ShowHelp = true; return ParseResult.Parsed; }
    private static ParseResult SetHistory(State state) { state.HistoryRequested = true; return ParseResult.Parsed; }
    private static ParseResult SetAllVariants(State state) { state.AllVariants = true; return ParseResult.Parsed; }
    private static ParseResult SetReferenceToolRequired(State state) { state.ReferenceToolRequired = true; return ParseResult.Parsed; }
}

internal static class BenchmarkOptionsTextWriterExtensions
{
    internal static bool WriteError(this TextWriter writer, string message)
    {
        writer.WriteLine(message);
        return false;
    }
}
