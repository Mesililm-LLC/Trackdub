using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.ReferenceTool;

/// <summary>
/// Runs an operator-provided NVIDIA reference executable (tensorrt_rtx or onnxruntime_perf_test)
/// against the same model the managed benchmark measured, capturing bounded stdout/stderr
/// evidence on disk. The result is always honest: statuses distinguish "tool missing",
/// "help output did not match the expected tool", "timed out", and "ran but produced no
/// metrics we recognize". Flags are verified against the tool's live --help text before
/// launch so a mismatched binary is never invoked with invented arguments.
/// </summary>
internal static class BenchmarkReferenceToolRunner
{
    private static readonly TimeSpan HelpTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(10);
    private const int CapturedTextLimitBytes = 256 * 1024;

    internal sealed record Request(
        BenchmarkReferenceToolKind Kind,
        string? ExplicitPath,
        bool Required,
        string ModelPath,
        string GraphRole,
        int RunCount,
        int InputSeed,
        bool DeviceBoundIo,
        string EvidenceDirectory);

    internal sealed record ToolRecipe(
        string ExecutableName,
        string HelpIdentitySubstring,
        Func<Request, string[]> RequiredFlags,
        Func<Request, string, string[]> BuildArguments,
        Func<Request, string, string[]> SkippedOptionalFlags,
        string TimingDomain);

    private sealed record ProcessOutcome(
        int ExitCode,
        string Stdout,
        bool StdoutTruncated,
        string Stderr,
        bool StderrTruncated,
        bool TimedOut);

    internal static async Task<BenchmarkReferenceToolResult> RunAsync(
        Request request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ToolRecipe recipe = RecipeFor(request.Kind);

        string? executablePath = ResolveExecutable(recipe.ExecutableName, request.ExplicitPath);
        if (executablePath is null)
        {
            return new BenchmarkReferenceToolResult
            {
                Tool = request.Kind,
                Status = BenchmarkReferenceStatus.NotAvailable,
                GraphRole = request.GraphRole,
                Required = request.Required,
                FailureReason = $"Executable '{recipe.ExecutableName}' not found on PATH and no --reference-tool-path was given."
            };
        }

        FileVersionInfo? executableVersion = null;
        try
        {
            executableVersion = FileVersionInfo.GetVersionInfo(executablePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }

        ProcessOutcome help = await RunProcessAsync(
            executablePath, ["--help"], null, null, HelpTimeout, cancellationToken).ConfigureAwait(false);
        string helpIdentity = $"sha256:{HashText(help.Stdout)}";
        string executableIdentity = DescribeExecutable(executableVersion);
        if (!help.Stdout.Contains(recipe.HelpIdentitySubstring, StringComparison.OrdinalIgnoreCase))
        {
            return new BenchmarkReferenceToolResult
            {
                Tool = request.Kind,
                Status = BenchmarkReferenceStatus.UnsupportedVersion,
                GraphRole = request.GraphRole,
                Required = request.Required,
                ExecutablePath = executablePath,
                ExecutableIdentity = executableIdentity,
                HelpIdentity = helpIdentity,
                Arguments = ["--help"],
                ExitCode = help.ExitCode,
                StandardOutputTruncated = help.StdoutTruncated,
                ElapsedMilliseconds = null,
                FailureReason = "The resolved executable's --help output does not identify the expected tool."
            };
        }

        // Argument gate: every flag the recipe intends to pass must be visible in --help.
        string[] missingFlags = recipe.RequiredFlags(request)
            .Where(flag => !help.Stdout.Contains(flag, StringComparison.Ordinal))
            .ToArray();
        if (missingFlags.Length > 0)
        {
            return new BenchmarkReferenceToolResult
            {
                Tool = request.Kind,
                Status = BenchmarkReferenceStatus.UnsupportedVersion,
                GraphRole = request.GraphRole,
                Required = request.Required,
                ExecutablePath = executablePath,
                ExecutableIdentity = executableIdentity,
                HelpIdentity = helpIdentity,
                Arguments = ["--help"],
                ExitCode = help.ExitCode,
                FailureReason = $"Tool help does not list required flag(s): {string.Join(", ", missingFlags)}."
            };
        }

        Directory.CreateDirectory(request.EvidenceDirectory);
        // Candidates (--all-variants) and repeat runs can share one report directory, so each
        // invocation gets its own evidence name; an overwritten log would orphan history evidence.
        string evidenceStem =
            $"reference-{request.Kind}-{request.GraphRole}-{ModelPathDigest(request.ModelPath)}-{Guid.NewGuid():N}";
        string stdoutLogPath = Path.Join(request.EvidenceDirectory, $"{evidenceStem}.stdout.log");
        string stderrLogPath = Path.Join(request.EvidenceDirectory, $"{evidenceStem}.stderr.log");

        string[] arguments = recipe.BuildArguments(request, help.Stdout);
        string[] skippedOptionalFlags = recipe.SkippedOptionalFlags(request, help.Stdout);

        var stopwatch = Stopwatch.StartNew();
        ProcessOutcome run = await RunProcessAsync(
            executablePath, arguments, stdoutLogPath, stderrLogPath, RunTimeout, cancellationToken)
            .ConfigureAwait(false);
        stopwatch.Stop();

        var metrics = ParseMetrics(request.Kind, run.Stdout);
        BenchmarkReferenceStatus status = run.TimedOut
            ? BenchmarkReferenceStatus.TimedOut
            : run.ExitCode != 0
                ? BenchmarkReferenceStatus.Failed
                : metrics.Count == 0
                    ? BenchmarkReferenceStatus.MetricsUnavailable
                    : BenchmarkReferenceStatus.Completed;

        var limitations = new List<string>
        {
            "Reference metrics come from the native tool's own timing domain; they are not "
                + "directly comparable to the managed end-to-end ORT latencies."
        };
        if (skippedOptionalFlags.Length > 0)
        {
            limitations.Add(
                $"Optional flag(s) not advertised by tool --help and skipped: {string.Join(", ", skippedOptionalFlags)}.");
        }

        if (run.StdoutTruncated || run.StderrTruncated)
        {
            limitations.Add("Captured output was truncated at the 256 KiB bound; the log files retain the full stream.");
        }

        bool stdoutLogWritten = File.Exists(stdoutLogPath);
        bool stderrLogWritten = File.Exists(stderrLogPath);
        return new BenchmarkReferenceToolResult
        {
            Tool = request.Kind,
            Status = status,
            GraphRole = request.GraphRole,
            Required = request.Required,
            ExecutablePath = executablePath,
            ExecutableIdentity = executableIdentity,
            HelpIdentity = helpIdentity,
            Arguments = arguments,
            ExitCode = run.ExitCode,
            StandardOutputLogPath = stdoutLogWritten ? stdoutLogPath : null,
            StandardErrorLogPath = stderrLogWritten ? stderrLogPath : null,
            StandardOutputIdentity = stdoutLogWritten ? $"sha256:{HashFile(stdoutLogPath)}" : null,
            StandardErrorIdentity = stderrLogWritten ? $"sha256:{HashFile(stderrLogPath)}" : null,
            StandardOutputTruncated = run.StdoutTruncated,
            StandardErrorTruncated = run.StderrTruncated,
            ElapsedMilliseconds = stopwatch.Elapsed.TotalMilliseconds,
            TimingDomain = recipe.TimingDomain,
            Metrics = metrics,
            Limitations = limitations,
            FailureReason = status switch
            {
                BenchmarkReferenceStatus.TimedOut => $"Reference tool did not finish within {RunTimeout}.",
                BenchmarkReferenceStatus.Failed => $"Reference tool exited with code {run.ExitCode}.",
                _ => null
            }
        };
    }

    private static string ModelPathDigest(string modelPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(modelPath)))[..12].ToLowerInvariant();

    internal static ToolRecipe RecipeFor(BenchmarkReferenceToolKind kind) => kind switch
    {
        // Flags grounded in the captured onnxruntime_perf_test --help on this machine.
        BenchmarkReferenceToolKind.OrtPerfTest => new ToolRecipe(
            ExecutableName: "onnxruntime_perf_test",
            HelpIdentitySubstring: "onnxruntime_perf_test",
            RequiredFlags: static request => request.DeviceBoundIo
                ? ["--e", "--m", "--r", "--s", "--I", "--S", "--g"]
                : ["--e", "--m", "--r", "--s", "--I", "--S"],
            BuildArguments: static (request, _) =>
            {
                var args = new List<string>
                {
                    "-e", "nvtensorrtrtx",
                    "-m", "times",
                    "-r", request.RunCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "-s",
                    "-I",
                    "-S", request.InputSeed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                };
                if (request.DeviceBoundIo)
                {
                    args.Add("-g");
                }

                args.Add(request.ModelPath);
                return args.ToArray();
            },
            SkippedOptionalFlags: static (_, _) => [],
            TimingDomain: "ort-e2e"),
        // --onnx / --loadEngine / --saveEngine are doc-verified for tensorrt_rtx;
        // trtexec-style tuning flags are passed only when the live --help advertises them.
        BenchmarkReferenceToolKind.TensorRtRtx => new ToolRecipe(
            ExecutableName: "tensorrt_rtx",
            HelpIdentitySubstring: "tensorrt_rtx",
            RequiredFlags: static _ => ["--onnx", "--iterations", "--warmup"],
            BuildArguments: static (request, helpText) =>
            {
                var args = new List<string>
                {
                    $"--onnx={request.ModelPath}",
                    $"--iterations={Math.Max(request.RunCount, 10)}",
                    "--warmup=200"
                };
                if (helpText.Contains("--useCudaGraph", StringComparison.Ordinal))
                {
                    args.Add("--useCudaGraph");
                }

                if (request.DeviceBoundIo
                    && helpText.Contains("--useSpinWait", StringComparison.Ordinal))
                {
                    args.Add("--useSpinWait");
                }

                return args.ToArray();
            },
            SkippedOptionalFlags: static (request, helpText) =>
            {
                var skipped = new List<string>();
                if (!helpText.Contains("--useCudaGraph", StringComparison.Ordinal))
                {
                    skipped.Add("--useCudaGraph");
                }

                if (request.DeviceBoundIo
                    && !helpText.Contains("--useSpinWait", StringComparison.Ordinal))
                {
                    skipped.Add("--useSpinWait");
                }

                return skipped.ToArray();
            },
            TimingDomain: "device-enqueue"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No reference tool recipe for this kind.")
    };

    private static string? ResolveExecutable(string executableName, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string fullPath = Path.GetFullPath(explicitPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string[] fileNames = OperatingSystem.IsWindows()
            ? [$"{executableName}.exe", executableName]
            : [executableName];
        foreach (string pathEntry in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string fileName in fileNames)
            {
                try
                {
                    string candidate = Path.Join(pathEntry.Trim(), fileName);
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                }
            }
        }

        return null;
    }

    private static string DescribeExecutable(FileVersionInfo? version) =>
        version is null
            ? "unknown"
            : $"{version.FileVersion ?? "unknown"}|{version.ProductVersion ?? "unknown"}";

    private static async Task<ProcessOutcome> RunProcessAsync(
        string fileName,
        string[] arguments,
        string? stdoutLogPath,
        string? stderrLogPath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuffer = new StringBuilder(CapturedTextLimitBytes / 4);
        var stderrBuffer = new StringBuilder(CapturedTextLimitBytes / 4);
        var stdoutTruncated = false;
        var stderrTruncated = false;

        await using var stdoutLog = stdoutLogPath is null ? null : new StreamWriter(stdoutLogPath, append: false);
        await using var stderrLog = stderrLogPath is null ? null : new StreamWriter(stderrLogPath, append: false);

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            stdoutLog?.WriteLine(eventArgs.Data);
            AppendBounded(stdoutBuffer, eventArgs.Data, ref stdoutTruncated);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is null)
            {
                return;
            }

            stderrLog?.WriteLine(eventArgs.Data);
            AppendBounded(stderrBuffer, eventArgs.Data, ref stderrTruncated);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            KillQuietly(process);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        int exitCode = process.HasExited ? process.ExitCode : -1;
        return new ProcessOutcome(exitCode, stdoutBuffer.ToString(), stdoutTruncated, stderrBuffer.ToString(), stderrTruncated, timedOut);
    }

    private static void KillQuietly(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void AppendBounded(StringBuilder buffer, string line, ref bool truncated)
    {
        if (buffer.Length + line.Length + 1 > CapturedTextLimitBytes)
        {
            truncated = true;
            return;
        }

        buffer.AppendLine(line);
    }

    /// <summary>Conservative metric extraction; unknown formats stay empty rather than guessed.</summary>
    internal static IReadOnlyList<BenchmarkReferenceMetric> ParseMetrics(
        BenchmarkReferenceToolKind kind, string stdoutText)
    {
        var metrics = new List<BenchmarkReferenceMetric>();
        if (kind is BenchmarkReferenceToolKind.OrtPerfTest)
        {
            AddMetric(metrics, @"Avg latency:\s*([\d.]+)\s*ms", stdoutText, "avg-latency", "ms", "ort-e2e");
            AddMetric(metrics, @"Total inference time cost:\s*([\d.]+)\s*s", stdoutText, "total-inference-time", "s", "ort-e2e");
            AddMetric(metrics, @"Total inference requests:\s*(\d+)", stdoutText, "inference-requests", "count", "ort-e2e");
            AddMetric(metrics, @"Session creation time cost:\s*([\d.]+)\s*s", stdoutText, "session-creation", "s", "session-init");
            foreach (Match match in Regex.Matches(stdoutText, @"Percentile Latency:\s*\{(?<entries>[^}]+)\}"))
            {
                foreach (Match entry in Regex.Matches(
                    match.Groups["entries"].Value, @"(?<name>P\d+):\s*(?<value>[\d.]+)\s*ms"))
                {
                    metrics.Add(new BenchmarkReferenceMetric(
                        $"latency-{entry.Groups["name"].Value.ToLowerInvariant()}",
                        double.Parse(entry.Groups["value"].Value, System.Globalization.CultureInfo.InvariantCulture),
                        "ms", "ort-e2e"));
                }
            }
        }
        else
        {
            AddMetric(metrics, @"GPU Compute Time:\s*min\s*=\s*([\d.]+)\s*ms,\s*max\s*=\s*([\d.]+)\s*ms,\s*mean\s*=\s*([\d.]+)\s*ms",
                stdoutText, "gpu-compute-mean", "ms", "gpu-compute", groupIndex: 3);
            AddMetric(metrics, @"GPU Compute Time:\s*min\s*=\s*([\d.]+)\s*ms", stdoutText, "gpu-compute-min", "ms", "gpu-compute");
            AddMetric(metrics, @"Throughput:\s*([\d.]+)\s*qps", stdoutText, "throughput", "qps", "throughput");
        }

        return metrics;
    }

    private static void AddMetric(
        List<BenchmarkReferenceMetric> metrics,
        string pattern,
        string text,
        string name,
        string unit,
        string domain,
        int groupIndex = 1)
    {
        Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
        if (match.Success
            && double.TryParse(match.Groups[groupIndex].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double value))
        {
            metrics.Add(new BenchmarkReferenceMetric(name, value, unit, domain));
        }
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
