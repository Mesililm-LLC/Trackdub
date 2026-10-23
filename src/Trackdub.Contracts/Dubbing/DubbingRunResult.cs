using Trackdub.Contracts.Diagnostics;

namespace Trackdub.Contracts.Dubbing;

/// <summary>
/// Overall status of a dubbing pipeline run.
/// </summary>
public enum DubbingRunStatus
{
    /// <summary>All stages completed successfully.</summary>
    Succeeded,

    /// <summary>Some stages succeeded but one or more were skipped or degraded.</summary>
    PartialSuccess,

    /// <summary>One or more stages failed with unrecoverable errors.</summary>
    Failed,

    /// <summary>Pre-flight validation failed before any stage executed.</summary>
    PreFlightFailed,
}

/// <summary>
/// Immutable result of a dubbing pipeline run, capturing timing, stage outcomes,
/// execution snapshot, and any pre-flight failures.
/// </summary>
public sealed record DubbingRunResult
{
    /// <summary>
    /// Unique identifier for this pipeline run.
    /// </summary>
    public required Guid RunId { get; init; }

    /// <summary>
    /// Correlation identifier for tracing across shell, engine, stage-run rows, and files.
    /// Defaults to <see cref="RunId"/> when not explicitly set by the caller.
    /// </summary>
    public Guid CorrelationId { get; init; }

    /// <summary>
    /// Timestamp when the pipeline run started.
    /// </summary>
    public required DateTimeOffset StartTime { get; init; }

    /// <summary>
    /// Timestamp when the pipeline run ended.
    /// </summary>
    public required DateTimeOffset EndTime { get; init; }

    /// <summary>
    /// Overall status of the pipeline run.
    /// </summary>
    public required DubbingRunStatus OverallStatus { get; init; }

    /// <summary>
    /// Per-stage outcome records in execution order.
    /// </summary>
    public required IReadOnlyList<StageOutcome> StageOutcomes { get; init; }

    /// <summary>
    /// Immutable snapshot of provider/model/voice decisions captured at run start.
    /// Keys are decision identifiers, values are the selected option identifiers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExecutionSnapshot { get; init; }

    /// <summary>
    /// List of pre-flight failure descriptions (missing model IDs, unavailable providers).
    /// Populated when <see cref="OverallStatus"/> is <see cref="DubbingRunStatus.PreFlightFailed"/>.
    /// </summary>
    public IReadOnlyList<string>? PreFlightFailures { get; init; }

    /// <summary>
    /// Cold-start evidence for this run: elapsed time from the pipeline start request to the
    /// first completed inference, decomposed by the phase (stage, smoke/warmup, unclassified)
    /// that produced it. Null when the result was built outside an instrumented pipeline run.
    /// </summary>
    public PipelineColdStartTiming? ColdStartTiming { get; init; }
}
