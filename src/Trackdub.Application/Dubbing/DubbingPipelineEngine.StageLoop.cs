using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Dubbing;

public sealed partial class DubbingPipelineEngine
{
    /// <summary>Session-scoped inputs every stage execution reads.</summary>
    private sealed record StageRunContext(
        IDubbingSession Session,
        DubbingSessionOptions Options,
        RuntimeModelSelections RuntimeSelections,
        Dictionary<string, string> ExecutionSnapshot);

    private sealed record StageRunTelemetry(
        IProgress<PipelineProgressEvent>? Progress,
        Guid ProjectId,
        Guid RunId);

    /// <summary>The ordered stages for this run and where their outcomes are collected.</summary>
    private sealed record StageLoopPlan(
        string[] StagesToRun,
        IReadOnlySet<string> DeclinedOptionalStages,
        List<StageOutcome> StageOutcomes);

    /// <summary>The ordered stages to run, and the inputs for choosing which later stage to warm up next.</summary>
    internal sealed record StageSchedule(
        string[] StagesToRun,
        RuntimeModelSelections RuntimeSelections,
        DubbingSessionOptions Options,
        IReadOnlySet<string> DeclinedOptionalStages);

    /// <summary>
    /// Per-stage hooks for <see cref="RunScheduledStagesAsync"/>: a gate that may return a skip
    /// outcome, the execution itself, where outcomes go, and where warmup skips are reported.
    /// </summary>
    internal sealed record StageCallbacks(
        Func<string, CancellationToken, Task<StageOutcome?>> GateStageAsync,
        Func<string, CancellationToken, Task<StageOutcome>> ExecuteStageAsync,
        Action<StageOutcome> RecordOutcome,
        IProgress<PipelineProgressEvent>? Progress);

    /// <summary>
    /// Runs each requested stage in order, honoring cancellation, failed prerequisites,
    /// and artifact resume. Outcomes are appended to <see cref="StageLoopPlan.StageOutcomes"/>.
    /// </summary>
    private async Task RunStageLoopAsync(
        StageRunContext run,
        StageRunTelemetry telemetry,
        StageLoopPlan plan,
        CancellationToken cancellationToken)
    {
        var loop = new StageLoop(this, run, telemetry, plan);
        try
        {
            await RunScheduledStagesAsync(
                new StageSchedule(plan.StagesToRun, run.RuntimeSelections, run.Options, plan.DeclinedOptionalStages),
                run.Session.Services?.GetService<IStageWarmupCoordinator>(),
                new StageCallbacks(loop.GateAsync, loop.ExecuteAsync, loop.Record, telemetry.Progress),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await loop.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stage sequence with next-stage warmup lookahead: the warmup for the next warmable
    /// stage is started (not awaited) immediately before the current stage executes, so
    /// its proving session create overlaps the current stage's work instead of sitting
    /// serially in the gap between stages. Each stage's gate runs before its pending
    /// warmup is joined, and a failed warmup verdict is applied only when the gate
    /// would let the stage execute: it skips that stage with
    /// <see cref="StageSkipReasonCodes.WarmupFailed"/> and the failure detail (the
    /// coordinator records no verdict for a failed smoke), while a passed or
    /// not-attempted warmup leaves execution untouched. Gate-skipped
    /// stages (cancelled, prerequisite-failed, declined, resume-valid) keep their
    /// gate outcome and start no new warmup, matching the previous post-stage
    /// scheduling for those paths.
    /// </summary>
    internal static async Task RunScheduledStagesAsync(
        StageSchedule schedule,
        IStageWarmupCoordinator? warmupCoordinator,
        StageCallbacks callbacks,
        CancellationToken cancellationToken)
    {
        var lookahead = new WarmupLookahead(warmupCoordinator, schedule);

        try
        {
            for (int i = 0; i < schedule.StagesToRun.Length; i++)
            {
                string stageName = schedule.StagesToRun[i];

                // Gate first: a gate-skipped stage (cancelled, prerequisite-failed,
                // declined, resume-valid) keeps its gate outcome even when its
                // lookahead warmup failed — the warmup verdict applies only to a
                // stage that would actually execute.
                StageOutcome? gateOutcome = await callbacks.GateStageAsync(stageName, cancellationToken).ConfigureAwait(false);
                if (gateOutcome is not null)
                {
                    await lookahead.DiscardIfTargetingAsync(stageName, cancellationToken).ConfigureAwait(false);
                    callbacks.RecordOutcome(gateOutcome);
                    continue;
                }

                StageOutcome? warmupSkip = await lookahead
                    .TakeFailedVerdictAsync(stageName, callbacks.Progress, cancellationToken)
                    .ConfigureAwait(false);
                if (warmupSkip is not null)
                {
                    callbacks.RecordOutcome(warmupSkip);
                    continue;
                }

                lookahead.StartConcurrentIfAllowed(i + 1, cancellationToken);
                StageOutcome outcome = await callbacks.ExecuteStageAsync(stageName, cancellationToken).ConfigureAwait(false);
                callbacks.RecordOutcome(outcome);
                lookahead.StartIfIdle(i + 1, cancellationToken);
            }
        }
        finally
        {
            await lookahead.ObserveRemainingAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gate, execution and outcome bookkeeping for one run's stage loop, including the
    /// optional TTS prefetch that overlaps translation.
    /// </summary>
    private sealed class StageLoop(
        DubbingPipelineEngine engine,
        StageRunContext run,
        StageRunTelemetry telemetry,
        StageLoopPlan plan) : IAsyncDisposable
    {
        private string? _failedPrerequisiteStage;
        private TtsStreamingPrefetch? _ttsPrefetch;

        public async Task<StageOutcome?> GateAsync(string stageName, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return Skip(stageName, "CANCELLED", "Cancelled");

            if (_failedPrerequisiteStage is not null)
            {
                return Skip(
                    stageName,
                    StageSkipReasonCodes.PrerequisiteFailed,
                    $"Skipped due to failed prerequisite: {_failedPrerequisiteStage}");
            }

            // Optional stages whose model the user declined during pre-flight provisioning
            // would fail mid-run without the model.
            if (plan.DeclinedOptionalStages.Contains(stageName))
            {
                return Skip(
                    stageName,
                    StageSkipReasonCodes.OptionalModelDeclined,
                    "Skipped — optional model setup declined");
            }

            bool hasValidArtifacts = !run.Options.ForceRerun &&
                await HasValidExistingArtifactsAsync(
                    run.Session,
                    run.Options,
                    stageName,
                    run.ExecutionSnapshot,
                    cancellationToken).ConfigureAwait(false);
            return hasValidArtifacts
                ? Skip(stageName, StageSkipReasonCodes.ExistingArtifactsValid, "Skipped — valid artifacts from prior run")
                : null;
        }

        public async Task<StageOutcome> ExecuteAsync(string stageName, CancellationToken cancellationToken)
        {
            await StartTtsPrefetchIfOverlappingAsync(stageName, cancellationToken).ConfigureAwait(false);

            StageOutcome outcome = await engine.ExecuteStageAsync(
                run.Session,
                run.Options,
                stageName,
                run.ExecutionSnapshot,
                run.RuntimeSelections,
                telemetry.Progress,
                telemetry.ProjectId,
                telemetry.RunId,
                cancellationToken,
                _ttsPrefetch).ConfigureAwait(false);

            if (IsTtsPrefetchSpent(stageName, outcome))
            {
                await DisposeAsync().ConfigureAwait(false);
            }

            return outcome;
        }

        public void Record(StageOutcome outcome)
        {
            plan.StageOutcomes.Add(outcome);
            if (IsBlockingPrerequisiteOutcome(outcome))
            {
                _failedPrerequisiteStage = outcome.StageName;
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_ttsPrefetch is null)
                return;

            await _ttsPrefetch.DisposeAsync().ConfigureAwait(false);
            _ttsPrefetch = null;
        }

        private StageOutcome Skip(string stageName, string reasonCode, string message)
        {
            ReportProgress(telemetry.Progress, stageName, PipelineProgressEventKind.Skipped, message);
            return BuildSkippedOutcome(stageName, reasonCode);
        }

        private async Task StartTtsPrefetchIfOverlappingAsync(string stageName, CancellationToken cancellationToken)
        {
            if (_ttsPrefetch is not null ||
                !ShouldOverlapTtsWithTranslation(
                    run.Options,
                    plan.StagesToRun,
                    Array.IndexOf(plan.StagesToRun, stageName),
                    plan.DeclinedOptionalStages))
            {
                return;
            }

            _ttsPrefetch = await TryCreateTtsPrefetchAsync(
                run.Session,
                run.Options,
                run.RuntimeSelections,
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Unclaimed clips are useless once TTS has run or translation produced no revision.
        /// </summary>
        private static bool IsTtsPrefetchSpent(string stageName, StageOutcome outcome) =>
            string.Equals(stageName, StageNames.Tts, StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(stageName, StageNames.Translation, StringComparison.OrdinalIgnoreCase) &&
             outcome.Status == StageStatus.Failed);
    }

    /// <summary>
    /// Tracks the single in-flight lookahead warmup and decides when to start, join or
    /// discard it.
    /// </summary>
    private sealed class WarmupLookahead(IStageWarmupCoordinator? coordinator, StageSchedule schedule)
    {
        private PendingStageWarmup? _pending;

        /// <summary>Joins and discards the verdict so no warmup task is left unobserved.</summary>
        public async Task DiscardIfTargetingAsync(string stageName, CancellationToken cancellationToken)
        {
            if (TakePendingFor(stageName) is { } pending)
            {
                await ObserveWarmupAsync(pending.Task, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<StageOutcome?> TakeFailedVerdictAsync(
            string stageName,
            IProgress<PipelineProgressEvent>? progress,
            CancellationToken cancellationToken)
        {
            if (TakePendingFor(stageName) is not { } pending)
                return null;

            StageWarmupResult? warmupResult =
                await AwaitWarmupResultAsync(pending.Task, cancellationToken).ConfigureAwait(false);
            StageOutcome? warmupSkip = BuildWarmupSkipOutcomeIfFailed(stageName, warmupResult);
            if (warmupSkip is not null)
            {
                ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped,
                    warmupResult?.Detail is { } detail
                        ? $"Skipped — stage warmup failed: {detail}"
                        : "Skipped — stage warmup failed");
            }

            return warmupSkip;
        }

        /// <summary>
        /// Capacity permitting, starts the next stage's proving warmup now so it overlaps this
        /// stage's execution; it is joined at the top of its target iteration.
        /// </summary>
        public void StartConcurrentIfAllowed(int nextIndex, CancellationToken cancellationToken)
        {
            if (ShouldStartConcurrentWarmup(coordinator, _pending))
            {
                _pending = Start(coordinator!, nextIndex, cancellationToken);
            }
        }

        /// <summary>
        /// At single-entry capacity (or without concurrent warmup), starts the next warmup only
        /// once the current stage has completed and released its model lease.
        /// </summary>
        public void StartIfIdle(int nextIndex, CancellationToken cancellationToken)
        {
            if (coordinator is not null && _pending is null)
            {
                _pending = Start(coordinator, nextIndex, cancellationToken);
            }
        }

        /// <summary>
        /// Stops joining warmup promptly on cancellation; the coordinator receives the same
        /// token and will stop at its next cancellable checkpoint.
        /// </summary>
        public async Task ObserveRemainingAsync(CancellationToken cancellationToken)
        {
            if (_pending is not null)
            {
                await ObserveWarmupAsync(_pending.Task, cancellationToken).ConfigureAwait(false);
            }
        }

        private PendingStageWarmup? TakePendingFor(string stageName)
        {
            if (_pending is null ||
                !string.Equals(_pending.StageName, stageName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            PendingStageWarmup pending = _pending;
            _pending = null;
            return pending;
        }

        private PendingStageWarmup? Start(
            IStageWarmupCoordinator warmupCoordinator,
            int nextIndex,
            CancellationToken cancellationToken) =>
            StartNextStageWarmup(
                warmupCoordinator,
                schedule.StagesToRun,
                nextIndex,
                schedule.RuntimeSelections,
                schedule.Options,
                schedule.DeclinedOptionalStages,
                cancellationToken);
    }
}
