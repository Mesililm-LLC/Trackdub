using System.Text.Json;

using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

internal static class CacheHandler
{
    public static async Task<int> ClearEnginesAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEngineCacheMaintenanceService maintenance = factory.GetRequiredService<IEngineCacheMaintenanceService>();
        EngineCacheClearResult result = maintenance.Clear();

        var payload = new
        {
            command = "cache clear engines",
            cacheDirectory = result.CacheDirectory,
            directoryExisted = result.DirectoryExisted,
            filesRemoved = result.FilesRemoved,
            filesSkipped = result.FilesSkipped,
            bytesFreed = result.BytesFreed,
            message = BuildClearEnginesMessage(result),
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    public static async Task<int> WarmEnginesAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        TextWriter progressOutput,
        IReadOnlyList<string>? modelPaths,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEpContextWarmupService warmup = factory.GetRequiredService<IEpContextWarmupService>();

        // Synchronous reporter on the progress stream: keeps stdout machine-readable and
        // guarantees every progress line is written before the JSON report line.
        var progress = new ImmediateProgress(progressOutput);
        EpContextWarmReport report = await warmup.WarmAsync(modelPaths, progress, provider, cancellationToken)
            .ConfigureAwait(false);

        var payload = new
        {
            command = "cache warm",
            provider = provider is ExecutionProviderKind.TensorRt ? "tensorrt" : "tensorrt-rtx",
            engineCacheDirectory = report.EngineCacheDirectory,
            environmentFingerprint = report.EnvironmentFingerprint,
            compiled = report.CompiledCount,
            reused = report.ReusedCount,
            skipped = report.SkippedCount,
            failed = report.FailedCount,
            items = report.Items.Select(item => new
            {
                sourceModelPath = item.SourceModelPath,
                status = item.Status,
                epContextPath = item.EpContextPath,
                compileMilliseconds = item.CompileMilliseconds,
                warmLoadMilliseconds = item.WarmLoadMilliseconds,
                detail = item.Detail,
            }),
            message = BuildWarmMessage(report, provider),
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return report.FailedCount > 0 ? Program.ExitPipelineFailure : Program.ExitSuccess;
    }

    private static string BuildWarmMessage(EpContextWarmReport report, ExecutionProviderKind provider) =>
        provider is ExecutionProviderKind.TensorRt
            ? $"Classic TensorRT engine-cache warm: compiled={report.CompiledCount}, reused={report.ReusedCount}, " +
              $"skipped={report.SkippedCount}, failed={report.FailedCount}."
            : $"EP-context warm: compiled={report.CompiledCount}, reused={report.ReusedCount}, " +
              $"skipped={report.SkippedCount}, failed={report.FailedCount}.";

    private sealed class ImmediateProgress(TextWriter progressOutput) : IProgress<string>
    {
        public void Report(string message)
        {
            progressOutput.WriteLine(message);
        }
    }

    private static string BuildClearEnginesMessage(EngineCacheClearResult result)
    {
        if (!result.DirectoryExisted)
        {
            return "Engine cache directory does not exist; nothing to clear.";
        }

        if (result.FilesRemoved == 0 && result.FilesSkipped == 0)
        {
            return "Engine cache directory exists but contained no files.";
        }

        string summary = result.FilesRemoved > 0
            ? $"Removed {result.FilesRemoved} engine cache file(s); freed {result.BytesFreed} bytes."
            : "No engine cache files could be removed.";

        if (result.FilesSkipped > 0)
        {
            summary += $" Skipped {result.FilesSkipped} file(s) due to access restrictions.";
        }

        return summary;
    }
}
