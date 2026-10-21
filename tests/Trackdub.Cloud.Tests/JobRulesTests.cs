using Trackdub.Cloud.Jobs;

namespace Trackdub.Cloud.Tests;

public sealed class JobRulesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-21T12:00:00Z");
    internal static JobRecord NewJob() => new()
    {
        Id = Guid.NewGuid(), OwnerId = "owner", RequestHash = "hash",
        Request = new(1, "Project", new(Guid.NewGuid()), "en", "es", "poc-server-v1"),
        Input = new("input", "v1", 20, new string('a', 64)),
        CreatedAt = Now, UpdatedAt = Now, QueueDeadlineAt = Now.AddHours(1),
    };
    private static ExecutionClaim Identity(JobRecord j, string id = "execution") => new(j.Id, j.AttemptId, j.DispatchId, id, j.Generation);

    [Fact]
    public void DuplicateExecutionCannotClaimEvenAfterLeaseExpiry()
    {
        var job = NewJob();
        var claimed = JobRules.Claim(job, Identity(job), Now);
        Assert.NotNull(claimed);
        Assert.Equal(JobState.Running, claimed.State);
        Assert.Null(JobRules.Claim(claimed, Identity(claimed, "duplicate"), Now.AddMinutes(10)));
    }

    [Fact]
    public void CancellationDoesNotClaimExecutionStopped()
    {
        var job = NewJob() with { State = JobState.Running, ExecutionId = "execution" };
        var cancel = JobRules.Cancel(job, Now);
        Assert.Equal(JobState.CancelRequested, cancel.State);
        Assert.False(cancel.IsTerminal);
        Assert.Equal(cancel, JobRules.Cancel(cancel, Now.AddSeconds(1)));
    }

    [Fact]
    public void StaleGenerationAndExpiredLeaseCannotPublish()
    {
        var job = NewJob();
        var claimed = JobRules.Claim(job, Identity(job), Now)!;
        var identity = Identity(claimed);
        Assert.True(JobRules.OwnsLease(claimed, identity, Now));
        Assert.False(JobRules.OwnsLease(claimed, identity with { Generation = identity.Generation - 1 }, Now));
        Assert.False(JobRules.OwnsLease(claimed, identity, Now.AddMinutes(2)));
    }

    [Fact]
    public void QueuedCancelPreventsClaim()
    {
        var job = NewJob();
        var cancelled = JobRules.Cancel(job, Now);
        Assert.Equal(JobState.Cancelled, cancelled.State);
        Assert.Null(JobRules.Claim(cancelled, Identity(cancelled), Now));
    }
}
