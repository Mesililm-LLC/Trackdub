using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubDubbingEngineArtifactMappingTests
{
    [Fact]
    public void ResolveStageArtifactPaths_SelectsCurrentRunAndProjectOnly()
    {
        StageRunRecord run = CreateRun();
        ProjectArtifact current = CreateArtifact(run, "artifacts/current.json");
        ProjectArtifact previous = current with { Id = Guid.NewGuid(), StageRunId = Guid.NewGuid(), RelativePath = "artifacts/previous.json" };
        ProjectArtifact foreign = current with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), RelativePath = "artifacts/foreign.json" };
        ProjectArtifact unlinked = current with { Id = Guid.NewGuid(), StageRunId = null, RelativePath = "artifacts/unlinked.json" };

        IReadOnlyList<string> paths = TrackdubDubbingEngine.ResolveStageArtifactPaths(
            [previous, foreign, unlinked, current], run);

        Assert.Equal(new[] { "artifacts/current.json" }, paths);
    }

    [Fact]
    public void ResolveStageArtifactPaths_MissingRunReturnsNoArtifacts()
    {
        StageRunRecord run = CreateRun();
        Assert.Empty(TrackdubDubbingEngine.ResolveStageArtifactPaths(
            [CreateArtifact(run, "artifacts/current.json")], null));
    }

    [Fact]
    public void ResolveStageArtifactPaths_PreviousRunCannotSatisfyNewRun()
    {
        StageRunRecord previous = CreateRun();
        StageRunRecord current = previous with { Id = Guid.NewGuid() };
        Assert.Empty(TrackdubDubbingEngine.ResolveStageArtifactPaths(
            [CreateArtifact(previous, "artifacts/previous.json")], current));
    }

    [Fact]
    public void ResolveStageArtifactPaths_UnlinkedArtifactsAreNotGuessed()
    {
        StageRunRecord run = CreateRun();
        ProjectArtifact artifact = CreateArtifact(run, "artifacts/nearby.json") with { StageRunId = null };
        Assert.Empty(TrackdubDubbingEngine.ResolveStageArtifactPaths([artifact], run));
    }

    [Fact]
    public void ResolveStageArtifactPaths_DeduplicatesAndOrdersPaths()
    {
        StageRunRecord run = CreateRun();
        IReadOnlyList<string> paths = TrackdubDubbingEngine.ResolveStageArtifactPaths(
            [CreateArtifact(run, "artifacts/z.json"), CreateArtifact(run, "artifacts/a.json"), CreateArtifact(run, "artifacts/z.json")], run);
        Assert.Equal(new[] { "artifacts/a.json", "artifacts/z.json" }, paths);
    }

    [Theory]
    [InlineData(StageRunStatus.Completed, StageStatus.Succeeded)]
    [InlineData(StageRunStatus.PartiallyCompleted, StageStatus.PartiallySucceeded)]
    [InlineData(StageRunStatus.Failed, StageStatus.Failed)]
    public void ResolveStageArtifactPaths_PreservesOutputsWithoutChangingStatus(
        StageRunStatus runStatus, StageStatus expectedStatus)
    {
        StageRunRecord run = CreateRun() with { Status = runStatus };
        Assert.Equal(new[] { "artifacts/partial.json" }, TrackdubDubbingEngine.ResolveStageArtifactPaths(
            [CreateArtifact(run, "artifacts/partial.json")], run));
        Assert.Equal(expectedStatus, TrackdubDubbingEngine.MapStageRunToSdkOutcome(run).Status);
    }

    [Fact]
    public void ResolveStageArtifactPaths_EmptyRunIdentityCannotClaimArtifacts()
    {
        StageRunRecord run = CreateRun() with { Id = Guid.Empty };
        Assert.Empty(TrackdubDubbingEngine.ResolveStageArtifactPaths(
            [CreateArtifact(run, "artifacts/invalid.json")], run));
    }

    [Fact]
    public void ResolveStageArtifactPaths_NullCollectionIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => TrackdubDubbingEngine.ResolveStageArtifactPaths(null!, CreateRun()));
    }

    private static StageRunRecord CreateRun() =>
        StageRunRecord.Start(Guid.NewGuid(), StageNames.Asr, DateTimeOffset.UnixEpoch)
            .Complete(DateTimeOffset.UnixEpoch.AddSeconds(1));

    private static ProjectArtifact CreateArtifact(StageRunRecord run, string path) =>
        new(Guid.NewGuid(), run.ProjectId, Guid.NewGuid(), ArtifactKind.TranscriptRevision,
            path, new string('0', 64), 1, null, null, null, run.StartedAtUtc, StageRunId: run.Id);
}
