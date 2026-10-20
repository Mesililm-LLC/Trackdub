using NSubstitute;
using Trackdub.Application.LipSynthesis;
using Trackdub.Contracts.LipSynthesis;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Domain.Media;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class LipStagePublicationTests
{
    [Fact]
    public async Task Alignment_segment_save_failure_cannot_leave_completed_run()
    {
        using var scope = new PublicationScope();
        var repository = Substitute.For<ILipSyncSegmentRepository>();
        repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<LipSyncSegment>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new IOException("Segment save failed.")));

        await Assert.ThrowsAsync<IOException>(() => scope.AlignmentHandler(repository)
            .HandleAsync(scope.AlignmentRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(StageRunStatus.Failed, Assert.Single(scope.StageRuns.All).Status);
        Assert.Equal(1, scope.StageRuns.UpdateCallCount);
        scope.AssertOriginalMediaPreserved();
    }

    [Fact]
    public async Task Synthesis_segment_save_cancellation_records_cancelled()
    {
        using var scope = new PublicationScope();
        using var cts = new CancellationTokenSource();
        var repository = Substitute.For<ILipSynthesisSegmentRepository>();
        repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<LipSynthesisSegment>>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                cts.Cancel();
                return Task.FromCanceled(cts.Token);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.SynthesisHandler(repository)
            .HandleAsync(scope.SynthesisRequest(), cts.Token));

        Assert.Equal(StageRunStatus.Canceled, Assert.Single(scope.StageRuns.All).Status);
        Assert.Equal(1, scope.StageRuns.UpdateCallCount);
        scope.AssertOriginalMediaPreserved();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Terminal_success_has_matching_segment_rows(bool synthesis, bool partial)
    {
        using var scope = new PublicationScope();
        Guid runIdUsedForSegmentSave = Guid.Empty;
        int saveCalls = 0;
        StageRunRecord acceptedRun;
        int persistedCount;
        const int expectedSegmentCount = 2;
        if (synthesis)
        {
            var persisted = new FakeLipSynthesisSegmentRepository();
            var repository = Substitute.For<ILipSynthesisSegmentRepository>();
            repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<LipSynthesisSegment>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Assert.Equal(StageRunStatus.Running, Assert.Single(scope.StageRuns.All).Status);
                    runIdUsedForSegmentSave = call.ArgAt<Guid>(1);
                    saveCalls++;
                    return persisted.SaveAllAsync(call.ArgAt<Guid>(0), runIdUsedForSegmentSave,
                        call.ArgAt<IReadOnlyList<LipSynthesisSegment>>(2), call.ArgAt<CancellationToken>(3));
                });
            var detector = new FakeFaceDetector();
            var faceDetector = Substitute.For<IFaceDetector>();
            faceDetector.IsAvailable.Returns(true);
            int detectionCalls = 0;
            faceDetector.DetectPrimaryFaceAsync(Arg.Any<FaceAnalysisRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    detector.FaceFound = !partial || ++detectionCalls != 2;
                    return detector.DetectPrimaryFaceAsync(call.ArgAt<FaceAnalysisRequest>(0), call.ArgAt<CancellationToken>(1));
                });
            var result = await scope.SynthesisHandler(repository, faceDetector)
                .HandleAsync(scope.SynthesisRequest(2), TestContext.Current.CancellationToken);
            acceptedRun = result.StageRun;
            var persistedSegments = await persisted.GetByStageRunAsync(acceptedRun.Id, TestContext.Current.CancellationToken);
            Assert.Equal(expectedSegmentCount, persistedSegments.Count);
            Assert.Equal(result.Segments, persistedSegments);
            persistedCount = persistedSegments.Count;
        }
        else
        {
            var persisted = new FakeLipSyncSegmentRepository();
            var repository = Substitute.For<ILipSyncSegmentRepository>();
            repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<LipSyncSegment>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Assert.Equal(StageRunStatus.Running, Assert.Single(scope.StageRuns.All).Status);
                    runIdUsedForSegmentSave = call.ArgAt<Guid>(1);
                    saveCalls++;
                    return persisted.SaveAllAsync(call.ArgAt<Guid>(0), runIdUsedForSegmentSave,
                        call.ArgAt<IReadOnlyList<LipSyncSegment>>(2), call.ArgAt<CancellationToken>(3));
                });
            var result = await scope.AlignmentHandler(repository)
                .HandleAsync(scope.AlignmentRequest(2, partial), TestContext.Current.CancellationToken);
            acceptedRun = result.StageRun;
            var persistedSegments = await persisted.GetByStageRunAsync(acceptedRun.Id, TestContext.Current.CancellationToken);
            Assert.Equal(expectedSegmentCount, persistedSegments.Count);
            Assert.Equal(result.Segments, persistedSegments);
            persistedCount = persistedSegments.Count;
        }

        Guid acceptedRunId = acceptedRun.Id;
        Assert.Equal(acceptedRunId, runIdUsedForSegmentSave);
        Assert.Equal(expectedSegmentCount, persistedCount);
        Assert.Equal(expectedSegmentCount, saveCalls);
        Assert.Equal(partial ? StageRunStatus.PartiallyCompleted : StageRunStatus.Completed, acceptedRun.Status);
        Assert.Equal(1, scope.StageRuns.CreateCallCount);
        Assert.Equal(1, scope.StageRuns.UpdateCallCount);
        int expectedArtifactCount = partial ? (synthesis ? 1 : 0) : expectedSegmentCount;
        Assert.Equal(expectedArtifactCount, scope.Media.Artifacts.Count);
        Assert.All(scope.Media.Artifacts, artifact =>
        {
            Assert.Equal(acceptedRunId, artifact.StageRunId);
            Assert.True(File.Exists(scope.Artifacts.GetPath(artifact.RelativePath)));
        });
        scope.AssertOriginalMediaPreserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelled_later_segment_preserves_prior_accepted_progress(bool synthesis)
    {
        using var scope = new PublicationScope();
        using var cts = new CancellationTokenSource();
        int saveCalls = 0;
        Guid runIdUsedForSegmentSave = Guid.Empty;
        Guid acceptedSegmentId = Guid.Empty;
        if (synthesis)
        {
            var persisted = new FakeLipSynthesisSegmentRepository();
            var repository = Substitute.For<ILipSynthesisSegmentRepository>();
            repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<LipSynthesisSegment>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (++saveCalls == 2)
                    {
                        cts.Cancel();
                        return Task.FromCanceled(cts.Token);
                    }
                    runIdUsedForSegmentSave = call.ArgAt<Guid>(1);
                    var segments = call.ArgAt<IReadOnlyList<LipSynthesisSegment>>(2);
                    acceptedSegmentId = Assert.Single(segments).SegmentId;
                    return persisted.SaveAllAsync(call.ArgAt<Guid>(0), runIdUsedForSegmentSave, segments, call.ArgAt<CancellationToken>(3));
                });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.SynthesisHandler(repository)
                .HandleAsync(scope.SynthesisRequest(2), cts.Token));
            var persistedSegments = await persisted.GetByStageRunAsync(runIdUsedForSegmentSave, TestContext.Current.CancellationToken);
            Assert.Single(persistedSegments);
            Assert.Equal(LipSynthesisSegmentStatus.Synthesized, persistedSegments[0].Status);
        }
        else
        {
            var persisted = new FakeLipSyncSegmentRepository();
            var repository = Substitute.For<ILipSyncSegmentRepository>();
            repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<LipSyncSegment>>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    if (++saveCalls == 2)
                    {
                        cts.Cancel();
                        return Task.FromCanceled(cts.Token);
                    }
                    runIdUsedForSegmentSave = call.ArgAt<Guid>(1);
                    var segments = call.ArgAt<IReadOnlyList<LipSyncSegment>>(2);
                    acceptedSegmentId = Assert.Single(segments).SegmentId;
                    return persisted.SaveAllAsync(call.ArgAt<Guid>(0), runIdUsedForSegmentSave, segments, call.ArgAt<CancellationToken>(3));
                });
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.AlignmentHandler(repository)
                .HandleAsync(scope.AlignmentRequest(2), cts.Token));
            var persistedSegments = await persisted.GetByStageRunAsync(runIdUsedForSegmentSave, TestContext.Current.CancellationToken);
            Assert.Single(persistedSegments);
            Assert.Equal(LipSyncSegmentStatus.Aligned, persistedSegments[0].Status);
        }

        var run = Assert.Single(scope.StageRuns.All);
        Assert.Equal(StageRunStatus.Canceled, run.Status);
        Assert.Equal(run.Id, runIdUsedForSegmentSave);
        Assert.Equal(1, scope.StageRuns.UpdateCallCount);
        var acceptedArtifact = Assert.Single(scope.Media.Artifacts,
            artifact => artifact.RelativePath.Contains(acceptedSegmentId.ToString("D"), StringComparison.Ordinal));
        Assert.Equal(run.Id, acceptedArtifact.StageRunId);
        Assert.True(File.Exists(scope.Artifacts.GetPath(acceptedArtifact.RelativePath)));
        scope.AssertOriginalMediaPreserved();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_required_repository_fails_before_processing(bool synthesis)
    {
        using var scope = new PublicationScope();
        if (synthesis)
            Assert.Throws<ArgumentNullException>(() => scope.SynthesisHandler(null!));
        else
            Assert.Throws<ArgumentNullException>(() => scope.AlignmentHandler(null!));
        Assert.Empty(scope.StageRuns.All);
        Assert.Empty(scope.Media.Artifacts);
        scope.AssertOriginalMediaPreserved();
    }

    [Theory]
    [InlineData(false, false, LipSynthesisSegmentStatus.SkippedLicenseGate)]
    [InlineData(true, true, LipSynthesisSegmentStatus.SkippedExperimentalGate)]
    [InlineData(true, false, LipSynthesisSegmentStatus.SkippedRuntimeUnavailable)]
    public async Task Synthesis_gate_rows_are_saved_before_skipped_terminal(
        bool licenseApproved, bool experimental, LipSynthesisSegmentStatus expectedStatus)
    {
        using var scope = new PublicationScope();
        var persisted = new FakeLipSynthesisSegmentRepository();
        var repository = Substitute.For<ILipSynthesisSegmentRepository>();
        repository.SaveAllAsync(Arg.Any<Guid>(), Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<LipSynthesisSegment>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                Assert.Equal(StageRunStatus.Running, Assert.Single(scope.StageRuns.All).Status);
                return persisted.SaveAllAsync(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1),
                    call.ArgAt<IReadOnlyList<LipSynthesisSegment>>(2), call.ArgAt<CancellationToken>(3));
            });
        var engine = new FakeLipSynthesisEngine { Available = false, Experimental = experimental };
        var handler = new LipSynthesisStageHandler(engine, new FakeFaceDetector(), new FakeFaceLandmarkProvider(),
            new FakeFacePoseEstimator(), scope.Artifacts, scope.StageRuns, repository);
        var result = await handler.HandleAsync(scope.SynthesisRequest(2) with { IsLicenseApproved = licenseApproved },
            TestContext.Current.CancellationToken);

        var persistedSegments = await persisted.GetByStageRunAsync(result.StageRun.Id, TestContext.Current.CancellationToken);
        const int expectedSegmentCount = 2;
        Assert.Equal(expectedSegmentCount, persistedSegments.Count);
        Assert.All(persistedSegments, segment => Assert.Equal(expectedStatus, segment.Status));
        Assert.Equal(StageRunStatus.Skipped, result.StageRun.Status);
        Assert.Equal(1, scope.StageRuns.UpdateCallCount);
        Assert.Equal(0, engine.CallCount);
        scope.AssertOriginalMediaPreserved();
    }

    private sealed class PublicationScope : IDisposable
    {
        private readonly string directory = Path.Join(Path.GetTempPath(), $"lip-publication-{Guid.NewGuid():N}");
        private readonly MediaAsset asset;
        private readonly string sourcePath;
        private readonly string dubbedPath;
        private readonly List<ProjectArtifact> originalArtifacts = [];
        public FakeArtifactStore Artifacts { get; }
        public FakeProjectStageRunStore StageRuns { get; } = new();
        public FakeMediaAssetRepository Media { get; } = new();

        public PublicationScope()
        {
            Directory.CreateDirectory(directory);
            Artifacts = new FakeArtifactStore(directory);
            sourcePath = Path.Join(directory, "original.mp4");
            dubbedPath = Path.Join(directory, "dubbed.wav");
            File.WriteAllBytes(sourcePath, [1, 2, 3]);
            File.WriteAllBytes(dubbedPath, [4, 5, 6]);
            asset = new MediaAsset(Guid.NewGuid(), Guid.NewGuid(), sourcePath, "original.mp4", "abc", 3,
                DateTimeOffset.UtcNow, "mp4", 10, true, true, DateTimeOffset.UtcNow);
        }

        public LipSyncStageHandler AlignmentHandler(ILipSyncSegmentRepository repository) => new(
            new FakeForcedAligner { PhonesToReturn = [new PhonemeTiming("AH", "arpabet", TimeSpan.Zero, TimeSpan.FromSeconds(1), 0.95)] },
            new FakePhonemeTimingPlanner(), new FakePhonemeStretchService(), Artifacts, StageRuns, repository,
            mediaAssetRepository: Media, audioClipExtractor: new FakeAudioClipExtractor());

        public LipSynthesisStageHandler SynthesisHandler(ILipSynthesisSegmentRepository repository, IFaceDetector? detector = null) => new(
            new FakeLipSynthesisEngine { OutputDirectory = Path.Join(directory, "engine") }, detector ?? new FakeFaceDetector(),
            new FakeFaceLandmarkProvider(), new FakeFacePoseEstimator(), Artifacts, StageRuns, repository, mediaAssetRepository: Media);

        public LipSyncStageRequest AlignmentRequest(int count = 1, bool partial = false)
        {
            var takes = new List<TtsTake>();
            for (int i = 0; i < count; i++)
            {
                var artifact = new ProjectArtifact(Guid.NewGuid(), asset.ProjectId, asset.Id, ArtifactKind.TtsTake,
                    $"tts/{Guid.NewGuid():N}.wav", "abc", 3, 1, null, null, DateTimeOffset.UtcNow);
                Artifacts.Seed(artifact.RelativePath);
                File.WriteAllBytes(Artifacts.GetPath(artifact.RelativePath), [7, 8, 9]);
                originalArtifacts.Add(artifact);
                takes.Add(TtsTake.CreateStock(asset.ProjectId, Guid.NewGuid(), Guid.NewGuid()) with { ArtifactId = artifact.Id });
            }
            return new LipSyncStageRequest(asset.ProjectId, asset, takes, originalArtifacts,
                SegmentSourceTimingMap: partial ? null : takes.ToDictionary(t => t.TranslatedSegmentId!.Value, _ => new SegmentSourceTiming(0, 1)),
                SourceSegmentTranscriptMap: takes.ToDictionary(t => t.TranslatedSegmentId!.Value, _ => "hello"), SourceAudioPath: dubbedPath);
        }

        public LipSynthesisStageRequest SynthesisRequest(int count = 1) => new(asset.ProjectId, asset, sourcePath, dubbedPath,
            Enumerable.Range(0, count).Select(i => new LipSynthesisTurn(Guid.NewGuid(), TimeSpan.FromSeconds(i), TimeSpan.FromSeconds(i + 1), "speaker")).ToArray());

        public void AssertOriginalMediaPreserved()
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(sourcePath));
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(dubbedPath));
            Assert.All(originalArtifacts, artifact => Assert.Equal(new byte[] { 7, 8, 9 }, File.ReadAllBytes(Artifacts.GetPath(artifact.RelativePath))));
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
