using Trackdub.Cloud.Jobs;
using Trackdub.TestDoubles;

namespace Trackdub.Cloud.Tests;

public sealed class JobServiceTests
{
    private static (FakeJobStore Store, FakeCloudArtifactStore Objects, JobService Service, CreateJobRequest Request) Setup()
    {
        var store = new FakeJobStore(); var objects = new FakeCloudArtifactStore();
        var id = Guid.NewGuid();
        store.Uploads[id] = new(id, "owner", new("upload/input.mp4", "v1", 10, new string('a', 64)), "upload/input.mp4", 10, new string('a', 64), "video/mp4", DateTimeOffset.UtcNow.AddMinutes(15));
        return (store, objects, new(store, objects, TimeProvider.System), new(1, "Project", new(id), "en", "es", "poc-server-v1"));
    }
    [Fact]
    public async Task FailedAcceptanceLeavesNoJobIntentOrIdempotency()
    {
        var s = Setup(); s.Store.FailAcceptance = true;
        var error = await Assert.ThrowsAsync<JobException>(() => s.Service.SubmitAsync("owner", "key", s.Request, TestContext.Current.CancellationToken));
        Assert.Equal(503, error.StatusCode); Assert.Empty(s.Store.Jobs); Assert.Empty(s.Store.Outbox); Assert.Empty(s.Store.Idempotency);
    }
    [Fact]
    public async Task SubmissionReplaySurvivesServiceRestartAndConflictIs409()
    {
        var s = Setup();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => s.Service.SubmitAsync("owner", "key", s.Request, TestContext.Current.CancellationToken)));
        Assert.Single(results.Select(j => j.Id).Distinct()); Assert.Single(s.Store.Outbox);
        var restarted = new JobService(s.Store, s.Objects, TimeProvider.System);
        Assert.Equal(results[0].Id, (await restarted.SubmitAsync("owner", "key", s.Request, TestContext.Current.CancellationToken)).Id);
        var error = await Assert.ThrowsAsync<JobException>(() => restarted.SubmitAsync("owner", "key", s.Request with { ProjectName = "Changed" }, TestContext.Current.CancellationToken));
        Assert.Equal(409, error.StatusCode);
    }
    [Fact]
    public async Task CrossOwnerAccessReturns404()
    {
        var s = Setup(); var job = await s.Service.SubmitAsync("owner", "key", s.Request, TestContext.Current.CancellationToken);
        Assert.Equal(404, (await Assert.ThrowsAsync<JobException>(() => s.Service.GetOwnedAsync("foreign", job.Id, TestContext.Current.CancellationToken))).StatusCode);
    }
    [Fact]
    public async Task OnlyOneConcurrentExecutionMayClaim()
    {
        var s = Setup(); var job = await s.Service.SubmitAsync("owner", "key", s.Request, TestContext.Current.CancellationToken);
        var claims = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => s.Service.ClaimAsync(new(job.Id, job.AttemptId, job.DispatchId, $"execution-{i}", 0), TestContext.Current.CancellationToken)));
        Assert.Single(claims, c => c is not null);
    }
    [Fact]
    public async Task IncompleteUploadNeverBecomesReady()
    {
        var s = Setup(); var uploads = new UploadService(s.Store, s.Objects, TimeProvider.System);
        var reservation = await uploads.CreateAsync("owner", new(10, new string('a', 64), "video/mp4"), TestContext.Current.CancellationToken);
        s.Objects.FailVerification = true;
        await Assert.ThrowsAsync<IOException>(() => uploads.CompleteAsync("owner", reservation.Id, "v1", TestContext.Current.CancellationToken));
        Assert.Null(s.Store.Uploads[reservation.Id].Object);
    }
    [Fact]
    public async Task LostSchedulerResponseIsRecoveredAfterRestartWithoutAnotherSubmit()
    {
        var s = Setup(); var job = await s.Service.SubmitAsync("owner", "key", s.Request, TestContext.Current.CancellationToken);
        var scheduler = new FakeJobScheduler { LoseSubmitResponse = true };
        var controller = new JobController(s.Store, s.Service, scheduler, s.Objects, TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => controller.TickAsync(TestContext.Current.CancellationToken));
        s.Store.Jobs[job.Id] = s.Store.Jobs[job.Id] with { DispatchLeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1) };
        await new JobController(s.Store, s.Service, scheduler, s.Objects, TimeProvider.System).TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, scheduler.Submissions);
    }
}
