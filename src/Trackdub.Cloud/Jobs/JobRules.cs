namespace Trackdub.Cloud.Jobs;

public static class JobRules
{
    public static JobRecord? Claim(JobRecord job, ExecutionClaim identity, DateTimeOffset now)
    {
        if (job.IsTerminal || job.CancelRequestedAt is not null || job.ExecutionId is not null
            || job.QueueDeadlineAt <= now || job.Id != identity.JobId || job.AttemptId != identity.AttemptId
            || job.DispatchId != identity.DispatchId || job.Generation != identity.Generation
            || string.IsNullOrWhiteSpace(identity.ExecutionId)) return null;
        return job with
        {
            State = JobState.Running,
            Phase = "staging",
            ExecutionId = identity.ExecutionId,
            Generation = job.Generation + 1,
            LeaseExpiresAt = now.AddSeconds(90),
            StartedAt = now,
            ExecutionDeadlineAt = now.AddHours(2),
            UpdatedAt = now,
        };
    }

    public static JobRecord Cancel(JobRecord job, DateTimeOffset now)
    {
        if (job.State == JobState.Cancelled || job.CancelRequestedAt is not null) return job;
        if (job.IsTerminal) throw new JobException("ALREADY_TERMINAL", 409, "Job already completed.");
        // A dispatch lease may already have authorized a remote submit, even with no execution ID yet.
        bool unlaunched = job.ExecutionId is null && job.DispatchLeaseExpiresAt is null;
        return job with
        {
            State = unlaunched ? JobState.Cancelled : JobState.CancelRequested,
            Phase = unlaunched ? null : job.Phase,
            CancelRequestedAt = now,
            UpdatedAt = now,
            CompletedAt = unlaunched ? now : null,
        };
    }

    public static bool OwnsLease(JobRecord job, ExecutionClaim claim, DateTimeOffset now) =>
        !job.IsTerminal && job.Id == claim.JobId && job.AttemptId == claim.AttemptId
        && job.DispatchId == claim.DispatchId && job.ExecutionId == claim.ExecutionId
        && job.Generation == claim.Generation && job.LeaseExpiresAt > now;
}
