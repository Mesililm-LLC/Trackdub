using System.Runtime.ExceptionServices;
using Trackdub.Application.Dubbing;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Sdk;

/// <summary>
/// Orchestrates sequential pipeline execution across multiple media files,
/// building a structured <see cref="BatchReport"/> with per-file outcomes.
/// </summary>
public sealed class BatchProcessor
{
    private readonly IDubbingPipelineEngine _engine;

    /// <summary>
    /// Creates a new <see cref="BatchProcessor"/> backed by the given dubbing engine.
    /// </summary>
    /// <param name="engine">Engine used to execute each file through the pipeline.</param>
    public BatchProcessor(TrackdubDubbingEngine engine)
        : this((IDubbingPipelineEngine)engine)
    {
    }

    /// <summary>
    /// Creates a new <see cref="BatchProcessor"/> backed by the given dubbing engine.
    /// </summary>
    /// <param name="engine">Engine used to execute each file through the pipeline.</param>
    /// <remarks>
    /// Hosts that already own an <see cref="IDubbingPipelineEngine"/> — the desktop shell wires
    /// one through its own DI container so the batch lane shares that host's session factory,
    /// model-selection providers, and licensing — use this overload instead of the
    /// <see cref="TrackdubDubbingEngine"/> one. That overload forces such a host to stand up a
    /// second headless container via <see cref="TrackdubBuilder"/>, which silently bypasses every
    /// override the host registered.
    /// </remarks>
    public BatchProcessor(IDubbingPipelineEngine engine)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
    }

    /// <summary>
    /// Execute the pipeline for each file in order. Returns a structured batch report.
    /// Each file's project directory is held under <see cref="ProjectLock"/> while it runs;
    /// a project locked by another run is reported as a failed file.
    /// </summary>
    /// <param name="mediaFiles">Ordered list of media file paths to process.</param>
    /// <param name="templateOptions">Template session options; SourceMediaPath is overridden per file.</param>
    /// <param name="batchOptions">Batch behavior configuration (fail-fast vs continue-on-error, output root).</param>
    /// <param name="progress">Optional progress reporter for engine stage-level events.</param>
    /// <param name="batchProgress">Optional progress reporter for per-file start and outcome boundaries.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="BatchReport"/> summarizing per-file outcomes and aggregate counts.</returns>
    public Task<BatchReport> ExecuteAsync(
        IReadOnlyList<string> mediaFiles,
        DubbingSessionOptions templateOptions,
        BatchOptions batchOptions,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken ct)
        => ExecuteAsync(mediaFiles, templateOptions, batchOptions, progress, ct, batchProgress: null);

    public async Task<BatchReport> ExecuteAsync(
        IReadOnlyList<string> mediaFiles,
        DubbingSessionOptions templateOptions,
        BatchOptions batchOptions,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken ct,
        IProgress<BatchFileProgressEvent>? batchProgress)
    {
        ArgumentNullException.ThrowIfNull(mediaFiles);
        ArgumentNullException.ThrowIfNull(templateOptions);
        ArgumentNullException.ThrowIfNull(batchOptions);

        var outcomes = new List<BatchFileOutcome>(mediaFiles.Count);
        int succeededCount = 0;
        int failedCount = 0;
        int skippedCount = 0;

        for (int i = 0; i < mediaFiles.Count; i++)
        {
            string filePath = mediaFiles[i];
            ReportFileProgress(batchProgress, i, filePath, BatchFileProgressKind.Started);

            // Outcomes are recorded and reported outside the processing try, so an exception from
            // batchProgress cannot turn a finished file into a second, failed outcome.
            BatchFileOutcome outcome;
            OperationCanceledException? cancellation = null;
            try
            {
                // Build per-file options from template
                DubbingSessionOptions fileOptions = BuildFileOptions(filePath, templateOptions, batchOptions);

                // Verify file accessibility before pipeline execution
                if (!File.Exists(filePath))
                {
                    throw new FileNotFoundException(
                        $"The media file was not found: '{filePath}'", filePath);
                }

                // Excludes a concurrent CLI or desktop run on the same project; a held lock
                // fails only this file (ProjectLockedException names the holder's PID).
                using ProjectLock projectLock = ProjectLock.Acquire(
                    TrackdubProjectPaths.ResolveProjectDirectory(filePath, fileOptions.ProjectOutputDirectory));

                DubbingRunResult result = await _engine.ExecuteAsync(fileOptions, progress, ct)
                    .ConfigureAwait(false);

                // Succeeded or PartialSuccess both count as success
                outcome = result.OverallStatus is DubbingRunStatus.Failed or DubbingRunStatus.PreFlightFailed
                    ? CreateOutcome(filePath, BatchFileStatus.Failed, BuildFailureReason(result))
                    : CreateOutcome(filePath, BatchFileStatus.Success);
            }
            catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
            {
                // Cancellation requested — mark current as failed, remaining as skipped, then propagate
                cancellation = ex;
                outcome = CreateOutcome(filePath, BatchFileStatus.Failed, "Processing cancelled.");
            }
            catch (FileNotFoundException ex)
            {
                outcome = CreateOutcome(filePath, BatchFileStatus.Failed, $"File not found: {ex.Message}");
            }
            catch (IOException ex)
            {
                outcome = CreateOutcome(filePath, BatchFileStatus.Failed, $"I/O error: {ex.Message}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = CreateOutcome(filePath, BatchFileStatus.Failed, ex.Message);
            }
            catch (OperationCanceledException ex)
            {
                // Cancellation raised with a foreign/unrelated token (or after ct is no
                // longer requested) — an OCE that does not match the requested-cancel guard
                // above. Record it as a per-file failure so cancellation always yields a
                // BatchReport instead of crashing the whole batch.
                outcome = CreateOutcome(filePath, BatchFileStatus.Failed, $"Processing cancelled: {ex.Message}");
            }

            outcomes.Add(outcome);
            if (outcome.Status == BatchFileStatus.Success)
            {
                succeededCount++;
                ReportFileProgress(batchProgress, i, filePath, BatchFileProgressKind.Succeeded);
            }
            else
            {
                failedCount++;
                ReportFileProgress(batchProgress, i, filePath, BatchFileProgressKind.Failed, outcome.Reason);
            }

            if (cancellation is not null)
            {
                skippedCount += MarkRemainingAsSkipped(mediaFiles, i + 1, outcomes, batchProgress);
                ExceptionDispatchInfo.Throw(cancellation);
            }

            if (outcome.Status == BatchFileStatus.Failed && !batchOptions.ContinueOnError)
            {
                skippedCount += MarkRemainingAsSkipped(mediaFiles, i + 1, outcomes, batchProgress);
                break;
            }
        }

        return new BatchReport
        {
            Files = outcomes,
            SucceededCount = succeededCount,
            FailedCount = failedCount,
            SkippedCount = skippedCount,
        };
    }

    private static BatchFileOutcome CreateOutcome(string filePath, BatchFileStatus status, string? reason = null) =>
        new()
        {
            FilePath = filePath,
            Status = status,
            Reason = reason,
        };

    private static DubbingSessionOptions BuildFileOptions(
        string filePath,
        DubbingSessionOptions template,
        BatchOptions batchOptions)
    {
        string? outputDirectory = batchOptions.OutputRoot is not null
            ? BatchOutputPaths.BuildProjectDirectory(filePath, batchOptions.OutputRoot)
            : null;

        return template with
        {
            SourceMediaPath = filePath,
            ProjectOutputDirectory = outputDirectory ?? template.ProjectOutputDirectory,
        };
    }

    private static string BuildFailureReason(DubbingRunResult result)
    {
        if (result.OverallStatus == DubbingRunStatus.PreFlightFailed
            && result.PreFlightFailures is { Count: > 0 })
        {
            return $"Pre-flight failed: {string.Join("; ", result.PreFlightFailures)}";
        }

        return "Pipeline execution failed.";
    }

    private static void ReportFileProgress(
        IProgress<BatchFileProgressEvent>? batchProgress,
        int fileIndex,
        string filePath,
        BatchFileProgressKind kind,
        string? reason = null)
    {
        batchProgress?.Report(new BatchFileProgressEvent
        {
            FileIndex = fileIndex,
            FilePath = filePath,
            Kind = kind,
            Reason = reason,
        });
    }

    private static int MarkRemainingAsSkipped(
        IReadOnlyList<string> mediaFiles,
        int startIndex,
        List<BatchFileOutcome> outcomes,
        IProgress<BatchFileProgressEvent>? batchProgress)
    {
        const string skipReason = "Skipped due to prior failure (fail-fast mode).";
        int count = 0;
        for (int j = startIndex; j < mediaFiles.Count; j++)
        {
            string filePath = mediaFiles[j];
            outcomes.Add(new BatchFileOutcome
            {
                FilePath = filePath,
                Status = BatchFileStatus.Skipped,
                Reason = skipReason,
            });
            ReportFileProgress(batchProgress, j, filePath, BatchFileProgressKind.Skipped, skipReason);
            count++;
        }

        return count;
    }
}
