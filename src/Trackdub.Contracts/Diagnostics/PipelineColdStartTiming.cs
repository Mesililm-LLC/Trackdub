namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Identifies the execution context an inference call runs under, so cold-start
/// evidence can separate smoke/warmup inference from real stage inference.
/// </summary>
public enum ColdStartPhase
{
    /// <summary>Inference ran outside any instrumented phase (e.g. ad-hoc or serve paths).</summary>
    Unclassified = 0,

    /// <summary>Inference ran as an execution-provider smoke test or warmup.</summary>
    Smoke = 1,

    /// <summary>Inference ran as real pipeline stage work.</summary>
    Stage = 2,
}

/// <summary>
/// Immutable cold-start evidence for one pipeline run: how long from the pipeline start
/// request to the first completed inference, decomposed by the phase that produced it.
/// All durations are measured from the pipeline start (the moment the run was requested)
/// and are null when the milestone was never reached within the run.
/// </summary>
public sealed record PipelineColdStartTiming
{
    /// <summary>Elapsed milliseconds from pipeline start to evidence snapshot (run end).</summary>
    public required double RunElapsedMs { get; init; }

    /// <summary>
    /// Elapsed milliseconds from pipeline start to the first stage entering execution
    /// (null when no stage started).
    /// </summary>
    public double? FirstStageStartElapsedMs { get; init; }

    /// <summary>Name of the first stage that entered execution.</summary>
    public string? FirstStageStartedStageName { get; init; }

    /// <summary>
    /// Elapsed milliseconds from pipeline start to the first completed inference of any
    /// phase (null when the run completed no inference).
    /// </summary>
    public double? FirstInferenceCompletionElapsedMs { get; init; }

    /// <summary>Run choke point (or caller label) that produced the first completed inference.</summary>
    public string? FirstInferenceSource { get; init; }

    /// <summary>Phase context of the first completed inference.</summary>
    public ColdStartPhase FirstInferencePhase { get; init; }

    /// <summary>Stage name associated with the first completed inference, when it ran under a stage phase.</summary>
    public string? FirstInferenceStageName { get; init; }

    /// <summary>
    /// Elapsed milliseconds from pipeline start to the first completed smoke/warmup
    /// inference (null when none ran). Smoke inference warms pooled sessions; it is
    /// evidence of cold-start cost, not of stage progress.
    /// </summary>
    public double? FirstSmokeInferenceCompletionElapsedMs { get; init; }

    /// <summary>Run choke point that produced the first completed smoke inference.</summary>
    public string? FirstSmokeInferenceSource { get; init; }

    /// <summary>
    /// Elapsed milliseconds from pipeline start to the first completed real stage
    /// inference (null when no stage inference completed). This is the headline
    /// pipeline-start to first-inference-completion measurement.
    /// </summary>
    public double? FirstStageInferenceCompletionElapsedMs { get; init; }

    /// <summary>Run choke point that produced the first completed stage inference.</summary>
    public string? FirstStageInferenceSource { get; init; }

    /// <summary>
    /// Elapsed milliseconds from pipeline start to the first completed inference that ran
    /// outside any instrumented phase (null when none ran).
    /// </summary>
    public double? FirstUnclassifiedInferenceCompletionElapsedMs { get; init; }

    /// <summary>Run choke point that produced the first completed unclassified inference.</summary>
    public string? FirstUnclassifiedInferenceSource { get; init; }
}
