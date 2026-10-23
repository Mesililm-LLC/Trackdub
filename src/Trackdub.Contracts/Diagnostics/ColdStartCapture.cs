using System.Diagnostics;

namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Always-on, one-shot recorder for pipeline cold-start evidence: how long from the
/// pipeline start request to the first completed inference. A capture is activated once
/// per pipeline run and flows with async work (like
/// <see cref="Benchmarking.BenchmarkPhaseCapture"/>), so inference that completes on any
/// worker thread within the run is observed. When no capture is active every mark is a
/// no-op, so inference code pays only a null check on the hot path.
/// </summary>
/// <remarks>
/// Completion marks are recorded by the run choke points of each inference backend
/// (ONNX Runtime run retry wrapper, GenAI token generation, worker-process results,
/// engines that call <c>Run</c> directly). The first completion wins per phase; later
/// marks are ignored so the evidence always describes the run's true first inference.
/// </remarks>
public sealed class ColdStartCapture
{
    private static readonly AsyncLocal<ColdStartCapture?> Ambient = new();
    private static readonly AsyncLocal<PhaseContext?> AmbientPhase = new();

    private readonly long startTimestamp = Stopwatch.GetTimestamp();
    private readonly object gate = new();

    private long? firstStageStartTimestamp;
    private string? firstStageStartedStageName;
    private InferenceMilestone? firstInference;
    private InferenceMilestone? smokeInference;
    private InferenceMilestone? stageInference;
    private InferenceMilestone? unclassifiedInference;
    private volatile bool smokeRecorded;
    private volatile bool stageRecorded;
    private volatile bool unclassifiedRecorded;
    private IApplicationLogger? logger;

    private sealed record PhaseContext(ColdStartPhase Phase, string? StageName)
    {
        public static readonly PhaseContext Unclassified = new(ColdStartPhase.Unclassified, null);
    }

    private sealed record InferenceMilestone(long Timestamp, string Source, PhaseContext Phase);

    /// <summary>The capture active on this async flow, or <see langword="null"/> when none.</summary>
    public static ColdStartCapture? Current => Ambient.Value;

    /// <summary>Phase context active on this async flow, or <see langword="null"/> outside any phase.</summary>
    public static ColdStartPhase? CurrentPhase => AmbientPhase.Value?.Phase;

    /// <summary>
    /// Logger captured by the run host once a session is available; used to emit the
    /// cold-start summary line at run end. Best-effort evidence, never required.
    /// </summary>
    public IApplicationLogger? Logger
    {
        get
        {
            lock (gate)
            {
                return logger;
            }
        }
    }

    /// <summary>Activates <paramref name="capture"/> for the calling async flow and everything it spawns.</summary>
    public static IDisposable Activate(ColdStartCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ColdStartCapture? previous = Ambient.Value;
        Ambient.Value = capture;
        return new CaptureScope(previous);
    }

    /// <summary>
    /// Marks the calling async flow as running inference under <paramref name="phase"/>,
    /// attributing any completion marks inside the scope. Disposal restores the previous
    /// phase context.
    /// </summary>
    public static IDisposable EnterPhase(ColdStartPhase phase, string? stageName = null)
    {
        PhaseContext? previous = AmbientPhase.Value;
        AmbientPhase.Value = new PhaseContext(phase, stageName);
        return new PhaseScope(previous);
    }

    /// <summary>Attaches the run host's logger for the end-of-run summary line.</summary>
    public void SetLogger(IApplicationLogger? logger)
    {
        lock (gate)
        {
            this.logger = logger;
        }
    }

    /// <summary>
    /// Records the first stage entering execution. Ignored when no capture is active or a
    /// first stage is already recorded.
    /// </summary>
    public static void MarkFirstStageStart(string stageName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        Ambient.Value?.RecordFirstStageStart(stageName);
    }

    /// <summary>
    /// Records the first completed inference at this choke point, attributed to the
    /// ambient phase context. No-op when no capture is active; later marks are ignored.
    /// </summary>
    /// <param name="source">
    /// Choke point or engine label that produced the completion (for example
    /// <c>onnx-run</c>, <c>smoke:genai</c>, <c>qwen3-tts:decode</c>,
    /// <c>inference-worker:tts</c>).
    /// </param>
    public static void MarkInferenceCompleted(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ColdStartCapture? capture = Ambient.Value;
        if (capture is null)
        {
            return;
        }

        capture.RecordInference(source, AmbientPhase.Value ?? PhaseContext.Unclassified);
    }

    /// <summary>Builds immutable evidence from the milestones recorded so far.</summary>
    public PipelineColdStartTiming BuildTiming()
    {
        lock (gate)
        {
            return new PipelineColdStartTiming
            {
                RunElapsedMs = ElapsedMs(startTimestamp, Stopwatch.GetTimestamp()),
                FirstStageStartElapsedMs = ElapsedSinceStart(firstStageStartTimestamp),
                FirstStageStartedStageName = firstStageStartedStageName,
                FirstInferenceCompletionElapsedMs = ElapsedSinceStart(firstInference?.Timestamp),
                FirstInferenceSource = firstInference?.Source,
                FirstInferencePhase = firstInference?.Phase.Phase ?? ColdStartPhase.Unclassified,
                FirstInferenceStageName = firstInference?.Phase.StageName,
                FirstSmokeInferenceCompletionElapsedMs = ElapsedSinceStart(smokeInference?.Timestamp),
                FirstSmokeInferenceSource = smokeInference?.Source,
                FirstStageInferenceCompletionElapsedMs = ElapsedSinceStart(stageInference?.Timestamp),
                FirstStageInferenceSource = stageInference?.Source,
                FirstUnclassifiedInferenceCompletionElapsedMs = ElapsedSinceStart(unclassifiedInference?.Timestamp),
                FirstUnclassifiedInferenceSource = unclassifiedInference?.Source,
            };
        }
    }

    /// <summary>Formats a one-line summary of <paramref name="timing"/> for run logs.</summary>
    public static string BuildSummaryMessage(PipelineColdStartTiming timing)
    {
        ArgumentNullException.ThrowIfNull(timing);
        if (timing.FirstInferenceCompletionElapsedMs is not { } firstInferenceMs)
        {
            return $"Cold start: no inference completed within the run (elapsed {timing.RunElapsedMs:F0} ms).";
        }

        string phase = timing.FirstInferencePhase switch
        {
            ColdStartPhase.Stage when timing.FirstInferenceStageName is not null =>
                $"stage:{timing.FirstInferenceStageName}",
            ColdStartPhase.Stage => "stage",
            ColdStartPhase.Smoke => "smoke",
            _ => "unclassified",
        };

        string stageStart = timing.FirstStageStartElapsedMs is { } stageStartMs
            ? $" First stage start at {stageStartMs:F0} ms (stage: {timing.FirstStageStartedStageName})."
            : " No stage started.";

        return $"Cold start: pipeline start -> first inference completed in {firstInferenceMs:F0} ms "
            + $"(source: {timing.FirstInferenceSource}, phase: {phase}).{stageStart}";
    }

    private void RecordFirstStageStart(string stageName)
    {
        lock (gate)
        {
            firstStageStartTimestamp ??= Stopwatch.GetTimestamp();
            firstStageStartedStageName ??= stageName;
        }
    }

    private void RecordInference(string source, PhaseContext phase)
    {
        // Fast path: this phase's milestone is already recorded. Inference choke points
        // like per-token decode loops mark on every step; after the first record those
        // marks must stay lock-free.
        if (phase.Phase switch
            {
                ColdStartPhase.Smoke => smokeRecorded,
                ColdStartPhase.Stage => stageRecorded,
                _ => unclassifiedRecorded,
            })
        {
            return;
        }

        lock (gate)
        {
            var milestone = new InferenceMilestone(Stopwatch.GetTimestamp(), source, phase);
            firstInference ??= milestone;
            switch (phase.Phase)
            {
                case ColdStartPhase.Smoke:
                    if (!smokeRecorded)
                    {
                        smokeRecorded = true;
                        smokeInference = milestone;
                    }

                    break;
                case ColdStartPhase.Stage:
                    if (!stageRecorded)
                    {
                        stageRecorded = true;
                        stageInference = milestone;
                    }

                    break;
                default:
                    if (!unclassifiedRecorded)
                    {
                        unclassifiedRecorded = true;
                        unclassifiedInference = milestone;
                    }

                    break;
            }
        }
    }

    private double? ElapsedSinceStart(long? milestoneTimestamp) =>
        milestoneTimestamp is { } timestamp ? ElapsedMs(startTimestamp, timestamp) : null;

    private static double ElapsedMs(long start, long end) =>
        Stopwatch.GetElapsedTime(start, end).TotalMilliseconds;

    private sealed class CaptureScope(ColdStartCapture? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }

    private sealed class PhaseScope(PhaseContext? previous) : IDisposable
    {
        public void Dispose() => AmbientPhase.Value = previous;
    }
}
