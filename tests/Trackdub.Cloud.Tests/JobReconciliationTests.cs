using Trackdub.Cloud.Jobs;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Jobs;
using Trackdub.TestDoubles;

namespace Trackdub.Cloud.Tests;

public sealed class JobReconciliationTests
{
    private static (FakeJobStore Store, FakeCloudArtifactStore Objects, FakeJobScheduler Scheduler, JobService Jobs, JobRecord Job, JobController Controller) Setup(int exit = 0, bool recover = false)
    {
        var store = new FakeJobStore(); var objects = new FakeCloudArtifactStore(); var scheduler = new FakeJobScheduler();
        var jobs = new JobService(store, objects, TimeProvider.System);
        var now = DateTimeOffset.UtcNow;
        var job = JobRulesTests.NewJob() with { State = JobState.Running, ExecutionId = "execution", Generation = 1,
            QueueDeadlineAt = now.AddHours(1), ExecutionDeadlineAt = now.AddHours(2), LeaseExpiresAt = now.AddSeconds(90) };
        var artifact = new WorkerArtifact("export.mp4", "video", 10, new string('b', 64));
        var result = new DubbingRunResult
        {
            RunId = Guid.NewGuid(), StartTime = now, EndTime = now, OverallStatus = DubbingRunStatus.Succeeded,
            StageOutcomes = new[] { "asr", "translation", "tts", "export" }.Select(s => new StageOutcome
            { StageName = s, Status = StageStatus.Succeeded, StartTime = now, EndTime = now, ArtifactPaths = s == "export" ? ["export.mp4"] : [] }).ToArray(),
        };
        objects.Outcome = new(1, "succeeded", result, null, null, [artifact]);
        var claim = new ExecutionClaim(job.Id, job.AttemptId, job.DispatchId, job.ExecutionId, job.Generation);
        var candidate = new AttemptOutcome(claim, new(JobService.AttemptPrefix(job) + "result.json", "v1", 100, new string('c', 64)), 0,
            [new(artifact.Path, "video", "export.mp4", "video/mp4", new(JobService.AttemptPrefix(job) + "export.mp4", "v1", 10, artifact.Sha256))]);
        objects.Candidate = candidate; job = job with { Candidate = recover ? null : candidate };
        store.Jobs[job.Id] = job; scheduler.Executions[job.DispatchId] = [new("execution", true, exit)];
        return (store, objects, scheduler, jobs, job, new(store, jobs, scheduler, objects, TimeProvider.System));
    }
    [Theory]
    [InlineData(139)][InlineData(137)][InlineData(2)]
    public async Task PrintedAndWrittenSuccessCannotOverrideAbnormalExit(int exit)
    {
        var s = Setup(exit); await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Failed, s.Store.Jobs[s.Job.Id].State);
        Assert.Empty(s.Store.Jobs[s.Job.Id].Artifacts);
    }
    [Fact]
    public async Task MissingCandidateAndResultCannotSucceed()
    {
        var s = Setup(recover: true); s.Objects.Candidate = null;
        await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Failed, s.Store.Jobs[s.Job.Id].State);
    }
    [Fact]
    public async Task LostCandidateWriteRecoversWithoutAnotherExecution()
    {
        var s = Setup(recover: true); await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Succeeded, s.Store.Jobs[s.Job.Id].State); Assert.Equal(0, s.Scheduler.Submissions);
    }
    [Fact]
    public async Task InterruptedArtifactVerificationRetriesWithoutResubmitting()
    {
        var s = Setup(); s.Objects.FailVerification = true;
        await Assert.ThrowsAsync<IOException>(() => s.Controller.TickAsync(TestContext.Current.CancellationToken));
        Assert.Equal(JobState.Running, s.Store.Jobs[s.Job.Id].State);
        s.Objects.FailVerification = false;
        await new JobController(s.Store, s.Jobs, s.Scheduler, s.Objects, TimeProvider.System).TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Succeeded, s.Store.Jobs[s.Job.Id].State); Assert.Equal(0, s.Scheduler.Submissions);
    }
    [Fact]
    public async Task CancellationCommittedDuringVerificationBlocksSuccess()
    {
        var s = Setup(); s.Objects.OnVerify = async () => await s.Jobs.CancelAsync("owner", s.Job.Id, TestContext.Current.CancellationToken);
        await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.CancelRequested, s.Store.Jobs[s.Job.Id].State);
        s.Objects.OnVerify = null; await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Cancelled, s.Store.Jobs[s.Job.Id].State);
    }
    [Fact]
    public async Task MissingSchedulerObservationDoesNotProveRunningCancellationStopped()
    {
        var s = Setup(); s.Scheduler.Executions[s.Job.DispatchId] = [];
        await s.Jobs.CancelAsync("owner", s.Job.Id, TestContext.Current.CancellationToken);
        await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.CancelRequested, s.Store.Jobs[s.Job.Id].State);
    }
    [Fact]
    public async Task StaleAttemptCannotReportCandidate()
    {
        var s = Setup(); var claim = s.Job.Candidate!.Claim with { Generation = 0 };
        Assert.Equal("LEASE_LOST", (await Assert.ThrowsAsync<JobException>(() => s.Jobs.ReportCandidateAsync(claim, s.Job.Candidate, TestContext.Current.CancellationToken))).Code);
    }
    [Fact]
    public async Task SkippedRequiredTtsCannotSucceed()
    {
        var s = Setup(); s.Objects.Outcome = s.Objects.Outcome! with { Result = s.Objects.Outcome.Result! with
            { StageOutcomes = s.Objects.Outcome.Result.StageOutcomes.Where(o => o.StageName != "tts").ToArray() } };
        await s.Controller.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(JobState.Failed, s.Store.Jobs[s.Job.Id].State);
    }
}
