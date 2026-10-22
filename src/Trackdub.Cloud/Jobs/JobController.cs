using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Jobs;

namespace Trackdub.Cloud.Jobs;

/// <summary>One bounded dispatcher/reconciler in the API host. No replacement is authorized while an execution may live.</summary>
public sealed class JobController(IJobStore store, JobService jobs, IJobScheduler scheduler, IArtifactStore artifacts, TimeProvider clock,
    Action<Guid, Exception>? onFailure = null)
{
    public async Task TickAsync(CancellationToken token)
    {
        foreach (Guid id in await store.DiscoverAsync(25, token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            var job = await store.GetAsync(id, token).ConfigureAwait(false);
            if (job is null || job.IsTerminal) continue;
            try { await ReconcileAsync(job, token).ConfigureAwait(false); }
            catch (Exception ex) when (onFailure is not null && ex is not OperationCanceledException) { onFailure(id, ex); }
        }
    }

    public async Task ReconcileAsync(JobRecord job, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        bool timedOut = (job.ExecutionDeadlineAt ?? job.QueueDeadlineAt) <= now;
        var executions = await scheduler.DescribeAsync(job.DispatchId, token).ConfigureAwait(false);
        bool leaseLost = job.ExecutionId is not null && job.LeaseExpiresAt <= now && executions.Any(e => !e.Stopped);
        if (job.CancelRequestedAt is not null || timedOut || leaseLost)
            await StopAndTerminateAsync(job, executions, timedOut, now, token).ConfigureAwait(false);
        else if (job.ExecutionId is null)
            await DispatchAsync(job, executions, now, token).ConfigureAwait(false);
        else
            await PublishAsync(job, executions, token).ConfigureAwait(false);
    }

    private async Task StopAndTerminateAsync(JobRecord job, IReadOnlyList<SchedulerExecution> executions, bool timedOut, DateTimeOffset now, CancellationToken token)
    {
        foreach (var execution in executions.Where(e => !e.Stopped))
            await scheduler.StopAsync(execution.Id, job.CancelRequestedAt is not null ? "Cancelled" : "Deadline or lease expired", token).ConfigureAwait(false);
        // An ambiguous submit with no visible execution is not evidence of cessation.
        bool stopped = executions.Count > 0 && executions.All(e => e.Stopped);
        bool neverDispatched = job.ExecutionId is null && job.DispatchLeaseExpiresAt is null;
        if (!stopped && !neverDispatched) return;
        await jobs.MutateAsync(job.Id, current => current.IsTerminal || current.AttemptId != job.AttemptId || current.Generation != job.Generation ? current : current with
        {
            State = current.CancelRequestedAt is not null ? JobState.Cancelled : timedOut ? JobState.TimedOut : JobState.Failed,
            CompletedAt = now,
            Phase = null,
            Error = new(TerminalCode(current, timedOut, job.StartedAt is null), "Execution stopped before completion.", false, null, current.Id.ToString()),
        }, token).ConfigureAwait(false);
    }

    private static string TerminalCode(JobRecord current, bool timedOut, bool neverStarted) =>
        current.CancelRequestedAt is not null ? "CANCELLED"
        : timedOut ? neverStarted ? "QUEUE_TIMEOUT" : "EXECUTION_TIMEOUT"
        : "LEASE_LOST";

    private async Task DispatchAsync(JobRecord job, IReadOnlyList<SchedulerExecution> executions, DateTimeOffset now, CancellationToken token)
    {
        if (executions.Count > 0 && executions.All(e => e.Stopped))
        {
            await FailAsync(job, "WORKER_CRASHED", token).ConfigureAwait(false);
            return;
        }
        if (job.DispatchLeaseExpiresAt > now) return;
        var leased = job with { DispatchLeaseExpiresAt = now.AddSeconds(90), Version = job.Version + 1, UpdatedAt = now };
        if (!await store.CompareExchangeAsync(leased, job.Version, token).ConfigureAwait(false)) return;
        if (!executions.Any(e => !e.Stopped))
            await scheduler.SubmitAsync(new(job.Id, job.AttemptId, job.DispatchId), token).ConfigureAwait(false);
        // The conditional start claim made by the worker owns execution identity, including a submit/claim race.
        await jobs.MutateAsync(job.Id, current => current.IsTerminal || current.ExecutionId is not null ? current
            : current with
            {
                State = current.CancelRequestedAt is null ? JobState.Queued : JobState.CancelRequested,
                Phase = "starting"
            }, token).ConfigureAwait(false);
    }

    private async Task PublishAsync(JobRecord job, IReadOnlyList<SchedulerExecution> executions, CancellationToken token)
    {
        foreach (var duplicate in executions.Where(e => e.Id != job.ExecutionId && !e.Stopped))
            await scheduler.StopAsync(duplicate.Id, "Duplicate execution", token).ConfigureAwait(false);
        var active = executions.SingleOrDefault(e => e.Id == job.ExecutionId);
        if (active is not { Stopped: true }) return;
        // Every execution sharing the dispatch must cease before publication.
        if (executions.Any(e => !e.Stopped)) return;
        if (active.ExitCode == 0 && job.Candidate is null)
        {
            var recoveredJob = await RecoverCandidateAsync(job, token).ConfigureAwait(false);
            if (recoveredJob is null)
            {
                await FailAsync(job, "RESULT_INVALID", token).ConfigureAwait(false);
                return;
            }
            job = recoveredJob;
        }
        if (active.ExitCode != 0 || job.Candidate is null || job.Candidate.CliExitCode != 0)
        {
            await FailAsync(job, active.ExitCode != 0 ? "WORKER_CRASHED" : "RESULT_MISSING", token).ConfigureAwait(false);
            return;
        }
        var candidate = job.Candidate;
        var outcome = await TryReadOutcomeAsync(candidate, token).ConfigureAwait(false);
        if (outcome is null || !OutcomeIsSuccessful(outcome, candidate))
        {
            await FailAsync(job, "RESULT_INVALID", token).ConfigureAwait(false);
            return;
        }
        if (!await TryVerifyArtifactsAsync(candidate, token).ConfigureAwait(false))
        {
            await FailAsync(job, "ARTIFACT_UPLOAD_FAILED", token).ConfigureAwait(false);
            return;
        }
        await SucceedAsync(job, candidate, outcome, token).ConfigureAwait(false);
    }

    // Returns the job with the recovered candidate, the unchanged job when nothing usable was recovered, or null when the stored candidate is invalid.
    private async Task<JobRecord?> RecoverCandidateAsync(JobRecord job, CancellationToken token)
    {
        AttemptOutcome? recovered;
        try { recovered = await artifacts.RecoverCandidateAsync(job, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or JobException) { return null; }
        if (recovered is null || recovered.Claim != new ExecutionClaim(job.Id, job.AttemptId, job.DispatchId, job.ExecutionId!, job.Generation))
            return job;
        string prefix = JobService.AttemptPrefix(job);
        if (!recovered.ResultObject.Key.StartsWith(prefix, StringComparison.Ordinal)
            || recovered.Artifacts.Any(a => !a.Object.Key.StartsWith(prefix, StringComparison.Ordinal)))
            return null;
        return await jobs.MutateAsync(job.Id, current => current.IsTerminal || current.CancelRequestedAt is not null
            || current.Generation != job.Generation ? current : current with { Candidate = recovered }, token).ConfigureAwait(false);
    }

    private async Task<WorkerRunOutcome?> TryReadOutcomeAsync(AttemptOutcome candidate, CancellationToken token)
    {
        try { return await artifacts.ReadOutcomeAsync(candidate.ResultObject, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or JobException) { return null; }
    }

    private async Task<bool> TryVerifyArtifactsAsync(AttemptOutcome candidate, CancellationToken token)
    {
        try
        {
            foreach (var artifact in candidate.Artifacts)
                await artifacts.VerifyAsync(artifact.Object, token).ConfigureAwait(false);
            return true;
        }
        catch (JobException) { return false; }
    }

    // Final CAS includes fresh cancellation and attempt identity checks. No late writer wins.
    private Task<JobRecord> SucceedAsync(JobRecord job, AttemptOutcome candidate, WorkerRunOutcome outcome, CancellationToken token) =>
        jobs.MutateAsync(job.Id, current => current.IsTerminal || current.CancelRequestedAt is not null
            || current.AttemptId != job.AttemptId || current.Generation != job.Generation || current.Candidate?.ResultObject != candidate.ResultObject
            ? current : current with
            {
                State = JobState.Succeeded,
                Phase = null,
                CompletedAt = clock.GetUtcNow(),
                Artifacts = candidate.Artifacts,
                Result = BuildResult(outcome, candidate),
            }, token);

    private static JobResult BuildResult(WorkerRunOutcome outcome, AttemptOutcome candidate) => new(1, outcome.Result!.RunId,
        outcome.Result.OverallStatus == DubbingRunStatus.Succeeded ? "succeeded" : "partialSuccess",
        outcome.Result.StageOutcomes.SelectMany(s => s.DegradationRecords ?? []).Take(16).Select(_ => "Stage degradation recorded; inspect the authorized run result.").ToArray(),
        candidate.Artifacts.Select(a => new ArtifactDto(a.Id, a.Kind, a.FileName, a.ContentType, a.Object.SizeBytes, a.Object.Sha256, true)).ToArray());

    private Task<JobRecord> FailAsync(JobRecord job, string code, CancellationToken token) => jobs.MutateAsync(job.Id,
        current => current.IsTerminal || current.CancelRequestedAt is not null || current.AttemptId != job.AttemptId
            ? current : current with
            {
                State = JobState.Failed,
                Phase = null,
                CompletedAt = clock.GetUtcNow(),
                Error = new(code, "Execution did not produce a verified result.", false, null, current.Id.ToString())
            }, token);

    public static bool OutcomeIsSuccessful(WorkerRunOutcome outcome, AttemptOutcome candidate)
    {
        if (candidate.CliExitCode != 0 || !PipelineGoalAchieved(outcome) || candidate.Artifacts.Count > 16) return false;
        return outcome.Artifacts.All(a => a.SizeBytes > 0 && a.Sha256.Length == 64 && candidate.Artifacts.Any(p =>
            p.Id == a.Path && p.Kind == a.Kind && p.Object.SizeBytes == a.SizeBytes && p.Object.Sha256 == a.Sha256));
    }

    public static bool PipelineGoalAchieved(WorkerRunOutcome outcome)
    {
        if (outcome is null || outcome.SchemaVersion != 1 || outcome.Status != "succeeded"
            || outcome.Result is null || outcome.Result.OverallStatus is not (DubbingRunStatus.Succeeded or DubbingRunStatus.PartialSuccess)
            || outcome.Result.StageOutcomes is null || outcome.Result.RunId == Guid.Empty
            || outcome.Artifacts is null || outcome.Artifacts.Count is 0 or > 15
            || outcome.Artifacts.Any(a => a is null || a.Kind is not ("video" or "subtitle") || string.IsNullOrWhiteSpace(a.Path)
                || a.SizeBytes <= 0 || a.Sha256 is not { Length: 64 } || !a.Sha256.All(Uri.IsHexDigit))
            || outcome.Artifacts.Select(a => a.Path).Distinct(StringComparer.Ordinal).Count() != outcome.Artifacts.Count) return false;
        // POC accepts no skipped/degraded required dubbing stage, regardless of generic CLI goal semantics.
        string[] required = ["ASR", "Translation", "TTS", "Export"];
        if (required.Any(name => !outcome.Result.StageOutcomes.Any(s => s is not null && string.Equals(s.StageName, name, StringComparison.OrdinalIgnoreCase)
            && s.Status == StageStatus.Succeeded))) return false;
        return outcome.Artifacts.Any(a => a.Kind == "video" && a.SizeBytes > 0);
    }
}
