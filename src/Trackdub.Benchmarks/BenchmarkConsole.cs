using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;

namespace Trackdub.Benchmarks;

public static class BenchmarkConsole
{
    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Trackdub.Benchmarks");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  Trackdub.Benchmarks ingest --project <path> [--name <project-name> --media <path> | --open] [--ffmpeg <path>] [--ffprobe <path>]");
        writer.WriteLine("  Trackdub.Benchmarks ingest --help");
        writer.WriteLine("  Trackdub.Benchmarks audio-prep --manifest <path> [--output <path>] [--format console|json|both]");
        writer.WriteLine("  Trackdub.Benchmarks audio-prep --help");
        writer.WriteLine("  Trackdub.Benchmarks controlled-matrix <fixture> --output <dir> [--stages <comma-separated>] [--model <stage=alias>] [--provider <kind>]");
        writer.WriteLine("  Trackdub.Benchmarks controlled-matrix --help");
        writer.WriteLine("  Trackdub.Benchmarks dubbing <input-path> [--language <code>] [--source-language <code>] [--output <dir>] [--force-rerun]");
        writer.WriteLine("  Trackdub.Benchmarks dubbing --batch <videos-dir> --languages fr,de [--source-language <code>] [--output <dir>] [--force-rerun]");
        writer.WriteLine("  Trackdub.Benchmarks dubbing --help");
        writer.WriteLine($"  Trackdub.Benchmarks tts-bench --jobs <jobs.jsonl> --results <results.jsonl> [--model <alias>] [--provider {string.Join("|", ExecutionProviderTokens.CliTags)}] [--warmup-runs <n>] [--repeat-runs <n>]");
        writer.WriteLine("  Trackdub.Benchmarks tts-bench --help");
        writer.WriteLine("  Trackdub.Benchmarks --help");
        writer.WriteLine($"  Trackdub.Benchmarks --model <path-or-scope> [--variant <name> | --all-variants] [--output <path>] [--provider cpu|auto|dml|migraphx|trt-rtx] [--windows-ml-device-policy {WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys("|")}] [--runs <n>] [--format console|json|both]");
        writer.WriteLine($"  Trackdub.Benchmarks --scope {TrtRtxSmokeCatalog.ScopeName} [--output <path>] [--provider trt-rtx] [--runs <n>] [--format console|json|both]");
        writer.WriteLine();
        writer.WriteLine("Ingest command:");
        writer.WriteLine("  --project <path>    Required project root, typically ending in .trackdub.");
        writer.WriteLine("  --name <name>       Required for create mode. Project display name.");
        writer.WriteLine("  --media <path>      Required for create mode. Source media file to ingest.");
        writer.WriteLine("  --open              Open an existing project and report source/artifact status.");
        writer.WriteLine("  --ffmpeg <path>     Optional explicit ffmpeg executable path.");
        writer.WriteLine("  --ffprobe <path>    Optional explicit ffprobe executable path.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --model <path-or-scope>  ONNX model path or scoped model reference under ./models.");
        writer.WriteLine($"  --scope {TrtRtxSmokeCatalog.ScopeName}  Run bundled starter-pack TRT RTX smoke targets (skips models not cached locally).");
        writer.WriteLine("  --output <path>     Output report path. Defaults to benchmark-report.json in the current directory.");
        writer.WriteLine("  --provider <name>   Provider preference: cpu, auto, dml, migraphx, qnn, or trt-rtx. Defaults to cpu.");
        writer.WriteLine("  --windows-ml-device-policy <name>  Windows ML EP device policy for catalog GPU runs. Defaults to explicit when omitted.");
        writer.WriteLine("  --runs <n>          Planned measured run count. Defaults to 5.");
        writer.WriteLine("  --variant <name>    Run a specific variant for the selected model reference.");
        writer.WriteLine("  --all-variants      Run every discovered benchmarkable variant and emit an aggregate report.");
        writer.WriteLine("  --format <name>     Output mode: console, json, or both. Defaults to both.");
        writer.WriteLine("  --help              Show this help.");
    }

    public static void WriteAudioPrepSummary(AudioPrepBenchmarkReport report, TextWriter writer)
    {
        writer.WriteLine($"Audio prep manifest: {report.ManifestPath}");
        writer.WriteLine($"Report path: {report.ReportPath}");
        writer.WriteLine($"Fixtures: {report.Aggregate.FixtureCount}");
        writer.WriteLine($"Auto comparisons: {report.Aggregate.AcceptedAutoCount}/{report.Aggregate.AutoComparisonCount} accepted");
        writer.WriteLine($"Average WER delta: {FormatRateDelta(report.Aggregate.AverageWordErrorRateDelta)}");
        writer.WriteLine($"Average CER delta: {FormatRateDelta(report.Aggregate.AverageCharacterErrorRateDelta)}");
        writer.WriteLine($"Average speech coverage delta: {FormatSeconds(report.Aggregate.AverageSpeechCoverageDeltaSeconds)}");
        writer.WriteLine($"Average turn fragmentation increase: {FormatFactor(report.Aggregate.AverageTurnFragmentationIncreaseRatio)}");

        foreach (AudioPrepBenchmarkFixtureReport fixture in report.Fixtures)
        {
            writer.WriteLine();
            writer.WriteLine($"Fixture: {fixture.FixtureId}");
            if (fixture.AutoComparison is null)
            {
                writer.WriteLine("  Auto comparison: n/a");
                continue;
            }

            writer.WriteLine($"  Auto accepted: {fixture.AutoComparison.Accepted}");
            writer.WriteLine($"  WER delta: {FormatRateDelta(fixture.AutoComparison.WordErrorRateDelta)}");
            writer.WriteLine($"  CER delta: {FormatRateDelta(fixture.AutoComparison.CharacterErrorRateDelta)}");
            writer.WriteLine($"  Speaker drift: {fixture.AutoComparison.SpeakerCountDrift?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "n/a"}");
        }
    }

    public static void WriteSummary(BenchmarkReport report, TextWriter writer)
    {
        writer.WriteLine($"Scenario: {report.Scenario}");
        writer.WriteLine($"Model: {report.ModelPath}");
        writer.WriteLine($"Status: {report.Status}");
        writer.WriteLine($"Supports execution: {report.SupportsExecution}");
        writer.WriteLine($"Requested provider: {report.RequestedProvider}");
        writer.WriteLine($"Selected provider: {report.SelectedProvider}");
        writer.WriteLine($"Requested runs: {report.RunCount}");
        writer.WriteLine($"Model size: {report.ModelSizeBytes} bytes");
        writer.WriteLine($"Cold load: {FormatMilliseconds(report.Measurements.ColdLoadMilliseconds)}");
        writer.WriteLine($"Warmup: {FormatMilliseconds(report.Measurements.WarmupMilliseconds)}");
        if (!string.IsNullOrWhiteSpace(report.Measurements.EngineCacheOutcome)
            || !string.IsNullOrWhiteSpace(report.Measurements.ColdLoadDominantPhase))
        {
            writer.WriteLine(
                $"Cold load attribution: engine cache={report.Measurements.EngineCacheOutcome ?? "unknown"} "
                + $"(+{report.Measurements.EngineCacheFilesAdded ?? 0} file(s), +{report.Measurements.EngineCacheBytesAdded ?? 0} byte(s)); "
                + $"dominant={report.Measurements.ColdLoadDominantPhase ?? "unknown"}");
        }
        writer.WriteLine($"Warm latency avg/min/max: {FormatMilliseconds(report.Measurements.WarmLatencyAverageMilliseconds)} / {FormatMilliseconds(report.Measurements.WarmLatencyMinimumMilliseconds)} / {FormatMilliseconds(report.Measurements.WarmLatencyMaximumMilliseconds)}");
        writer.WriteLine($"Audio duration: {FormatSeconds(report.Measurements.AudioDurationSeconds)}");
        writer.WriteLine($"Real-time factor: {FormatFactor(report.Measurements.RealTimeFactorAverage)}");
        writer.WriteLine($"Report path: {report.ReportPath}");

        if (!string.IsNullOrWhiteSpace(report.FailureReason))
        {
            writer.WriteLine($"Failure reason: {report.FailureReason}");
        }

        if (report.Notes.Count > 0)
        {
            writer.WriteLine("Notes:");
            foreach (var note in report.Notes)
            {
                writer.WriteLine($"  - {note}");
            }
        }
    }

    public static void WriteBatchSummary(BenchmarkBatchReport report, TextWriter writer)
    {
        writer.WriteLine($"Batch reference: {report.RequestedReference}");
        writer.WriteLine($"Batch report path: {report.ReportPath}");
        writer.WriteLine($"Batch results: {report.Results.Count}");

        foreach (BenchmarkReport result in report.Results)
        {
            writer.WriteLine();
            WriteSummary(result, writer);
        }
    }

    private static string FormatMilliseconds(double? value) =>
        value is null ? "n/a" : $"{value.Value:F2} ms";

    private static string FormatSeconds(double? value) =>
        value is null ? "n/a" : $"{value.Value:F3} s";

    private static string FormatFactor(double? value) =>
        value is null ? "n/a" : $"{value.Value:F3}x";

    private static string FormatRateDelta(double? value) =>
        value is null ? "n/a" : $"{value.Value:+0.000;-0.000;0.000}";

    public static void WriteDubbingUsage(TextWriter writer)
    {
        writer.WriteLine("Trackdub Dubbing Benchmark");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  Single: Trackdub.Benchmarks dubbing <input-path> [--language <code>] [--source-language <code>] [--output <dir>] [--force-rerun]");
        writer.WriteLine("  Batch:  Trackdub.Benchmarks dubbing --batch <videos-dir> --languages fr,de,it,ja [--source-language <code>] [--output <dir>] [--force-rerun]");
        writer.WriteLine("  Help:   Trackdub.Benchmarks dubbing --help");
        writer.WriteLine();
        writer.WriteLine("Single Arguments:");
        writer.WriteLine("  <input-path>              Path to the video file to benchmark (required)");
        writer.WriteLine("  --language <code>         Target language code (default: es)");
        writer.WriteLine("  --source-language <code>  Source language code (default: auto-detect)");
        writer.WriteLine("  --output <dir>            Root directory for reports and project subfolders (optional)");
        writer.WriteLine("  --force-rerun             Re-execute all stages even if valid artifacts exist");
        writer.WriteLine("  --model <stage>=<alias>   Pin a model alias for a stage (repeatable, e.g. --model asr=whisper-small)");
        writer.WriteLine("  --provider <stage>=<kind> Pin an execution provider for a stage (repeatable, e.g. --provider tts=directml)");
        writer.WriteLine();
        writer.WriteLine("Batch Arguments:");
        writer.WriteLine("  --batch <dir>             Directory containing media files (required)");
        writer.WriteLine("  --languages <codes>       Comma-separated target language codes, e.g. fr,de,it,ja");
        writer.WriteLine("  --source-language <code>  Source language code (default: auto-detect)");
        writer.WriteLine("  --output <dir>            Root directory for reports and project subfolders (optional)");
        writer.WriteLine("  --force-rerun             Re-execute all stages even if valid artifacts exist");
        writer.WriteLine();
        writer.WriteLine("Examples:");
        writer.WriteLine("  Trackdub.Benchmarks dubbing \"B:\\OneDrive\\Videos\\Movies\\clip.mp4\" --language es");
        writer.WriteLine("  Trackdub.Benchmarks dubbing --batch \"B:\\OneDrive\\Videos\\Movies\" --languages fr,de,it,ja --source-language en");
    }

    public static void WriteDubbingSummary(DubbingBenchmarkReport report, TextWriter writer)
    {
        writer.WriteLine("╔════════════════════════════════════════════════════════════════╗");
        writer.WriteLine("║           Trackdub Dubbing Benchmark Results                   ║");
        writer.WriteLine("╚════════════════════════════════════════════════════════════════╝");
        writer.WriteLine();
        writer.WriteLine($"Input:         {report.InputPath}");
        writer.WriteLine($"Language:      {report.TargetLanguage}");
        writer.WriteLine($"Segments:      {report.SegmentCount}");
        writer.WriteLine();
        writer.WriteLine("Performance:");
        writer.WriteLine($"  Total Time:   {report.TotalDuration.TotalSeconds:F2}s");
        writer.WriteLine($"  ASR:          {report.AsrDuration.TotalSeconds:F2}s ({report.AsrDuration.TotalSeconds / report.TotalDuration.TotalSeconds:P1})");
        writer.WriteLine($"  Translation:  {report.TranslationDuration.TotalSeconds:F2}s ({report.TranslationDuration.TotalSeconds / report.TotalDuration.TotalSeconds:P1})");
        writer.WriteLine($"  TTS:          {report.TtsDuration.TotalSeconds:F2}s ({report.TtsDuration.TotalSeconds / report.TotalDuration.TotalSeconds:P1})");
        writer.WriteLine($"  Mixing:       {report.MixingDuration.TotalSeconds:F2}s ({report.MixingDuration.TotalSeconds / report.TotalDuration.TotalSeconds:P1})");
        writer.WriteLine();
        writer.WriteLine("Hardware:");
        writer.WriteLine($"  {report.HardwareInfo}");
        writer.WriteLine();
        writer.WriteLine($"Started:  {report.StartedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        writer.WriteLine($"Finished: {report.CompletedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        writer.WriteLine($"Duration: {report.CompletedAtUtc - report.StartedAtUtc}");
    }

    public static void WriteDubbingBatchSummary(
        IReadOnlyList<DubbingBenchmarkReport> reports,
        TextWriter writer)
    {
        int successCount = reports.Count(r => r.Success);
        int failCount = reports.Count - successCount;

        var byLanguage = reports
            .Where(r => r.Success)
            .GroupBy(r => r.TargetLanguage)
            .ToDictionary(
                g => g.Key,
                g => new
                {
                    Count = g.Count(),
                    AvgTotal = TimeSpan.FromTicks((long)g.Average(r => r.TotalDuration.Ticks)),
                    AvgAsr = TimeSpan.FromTicks((long)g.Average(r => r.AsrDuration.Ticks)),
                    AvgTranslation = TimeSpan.FromTicks((long)g.Average(r => r.TranslationDuration.Ticks)),
                    AvgTts = TimeSpan.FromTicks((long)g.Average(r => r.TtsDuration.Ticks)),
                    AvgMixing = TimeSpan.FromTicks((long)g.Average(r => r.MixingDuration.Ticks)),
                });

        writer.WriteLine();
        writer.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        writer.WriteLine("║           Dubbing Batch Benchmark Summary                 ║");
        writer.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        writer.WriteLine();
        writer.WriteLine($"Total runs:   {reports.Count}");
        writer.WriteLine($"Succeeded:    {successCount}");
        writer.WriteLine($"Failed:       {failCount}");
        writer.WriteLine();

        foreach (var kv in byLanguage)
        {
            writer.WriteLine($"  Language: {kv.Key}  (n={kv.Value.Count})");
            writer.WriteLine($"    Total avg:    {kv.Value.AvgTotal.TotalSeconds:F2}s");
            writer.WriteLine($"    ASR avg:      {kv.Value.AvgAsr.TotalSeconds:F2}s");
            writer.WriteLine($"    Translation:  {kv.Value.AvgTranslation.TotalSeconds:F2}s");
            writer.WriteLine($"    TTS avg:      {kv.Value.AvgTts.TotalSeconds:F2}s");
            writer.WriteLine($"    Mixing avg:   {kv.Value.AvgMixing.TotalSeconds:F2}s");
            writer.WriteLine();
        }

        if (failCount > 0)
        {
            writer.WriteLine("Failed runs:");
            foreach (var r in reports.Where(r => !r.Success))
            {
                writer.WriteLine($"    [FAIL] {Path.GetFileName(r.InputPath)} / {r.TargetLanguage}: {r.Error}");
            }
        }
    }
}
