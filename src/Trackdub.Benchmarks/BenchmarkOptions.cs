using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;

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
    public static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter errorWriter,
        out BenchmarkOptions options)
    {
        string? modelPath = null;
        var outputPath = Path.Join(Environment.CurrentDirectory, "benchmark-report.json");
        var providerPreference = BenchmarkProviderPreference.Cpu;
        var runCount = 5;
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
                ShowHelp: true);
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

        options = new BenchmarkOptions(
            string.IsNullOrWhiteSpace(modelPath) ? string.Empty : modelPath.Trim(),
            Path.GetFullPath(outputPath),
            providerPreference,
            runCount,
            string.IsNullOrWhiteSpace(variant) ? null : variant,
            allVariants,
            reportFormat,
            windowsMlDevicePolicyKey,
            string.IsNullOrWhiteSpace(scope) ? null : scope,
            ShowHelp: false,
            Mock: mock,
            DryRun: dryRun);

        return true;
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
