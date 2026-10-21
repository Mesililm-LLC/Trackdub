using Trackdub.Application.LipSynthesis;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Domain.Media;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.LipSynthesis;

/// <summary>
/// Stage-level real-model proof for M23: real LatentSync engine + real SCRFD/landmark/pose stack,
/// driven through <see cref="LipSynthesisStageHandler"/>. Skips unless models and fixtures are
/// present. Never downloads.
/// </summary>
public sealed class LipSynthesisRealEngineIntegrationTests
{
    [LipSynthesisRealModelFact]
    [Trait("Category", "Integration")]
    public async Task HandleAsync_RealEngineAndFaceStack_ProducesLipSynthesisTakeOrHonestSkip()
    {
        string videoPath = Environment.GetEnvironmentVariable(LipSynthesisIntegrationSupport.VideoFixtureEnvVar)!;
        string audioPath = Environment.GetEnvironmentVariable(LipSynthesisIntegrationSupport.AudioFixtureEnvVar)!;
        LipSynthesisIntegrationSupport.RealLipSynthesisStack stack = LipSynthesisIntegrationSupport.CreateRealStack();

        string directory = Path.Join(Path.GetTempPath(), $"lipsynth-stage-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = new MediaAsset(
                Id: Guid.NewGuid(),
                ProjectId: projectId,
                SourceFilePath: videoPath,
                SourceFileName: Path.GetFileName(videoPath),
                FingerprintSha256: "integration-fixture",
                SourceSizeBytes: new FileInfo(videoPath).Length,
                SourceLastWriteTimeUtc: File.GetLastWriteTimeUtc(videoPath),
                FormatName: Path.GetExtension(videoPath).TrimStart('.'),
                DurationSeconds: 10.0,
                HasAudio: true,
                HasVideo: true,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var mediaRepo = new FakeMediaAssetRepository();

            var handler = new LipSynthesisStageHandler(
                lipSynthesisEngine: stack.Engine,
                faceDetector: stack.FaceDetector,
                faceLandmarkProvider: stack.FaceLandmarkProvider,
                facePoseEstimator: stack.FacePoseEstimator,
                artifactStore: artifactStore,
                stageRunStore: stageRunStore,
                segmentRepository: new FakeLipSynthesisSegmentRepository(),
                mediaAssetRepository: mediaRepo);

            var turn = new LipSynthesisTurn(
                SegmentId: Guid.NewGuid(),
                Start: TimeSpan.FromSeconds(0.5),
                End: TimeSpan.FromSeconds(2.5),
                SpeakerId: "spk-integration");

            LipSynthesisStageResult result = await handler.HandleAsync(
                new LipSynthesisStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    SourceVideoPath: videoPath,
                    DubbedAudioPath: audioPath,
                    SpeakerTurns: [turn],
                    IsEnabled: true,
                    IsLicenseApproved: true,
                    AllowExperimentalExecution: true),
                TestContext.Current.CancellationToken);

            LipSynthesisSegment segment = Assert.Single(result.Segments);

            Assert.True(
                segment.Status is LipSynthesisSegmentStatus.Synthesized
                    or LipSynthesisSegmentStatus.SkippedNoFace
                    or LipSynthesisSegmentStatus.SkippedNonFrontal
                    or LipSynthesisSegmentStatus.SkippedLowConfidence
                    or LipSynthesisSegmentStatus.SkippedOccluded
                    or LipSynthesisSegmentStatus.SkippedUnstableCrop,
                $"Unexpected segment status {segment.Status}: skip='{segment.SkipReason}' fail='{segment.FailureReason}'");
            if (LipSynthesisIntegrationSupport.RequiresSynthesizedOutcome())
            {
                Assert.Equal(LipSynthesisSegmentStatus.Synthesized, segment.Status);
            }

            Assert.NotEqual(LipSynthesisSegmentStatus.Failed, segment.Status);
            Assert.True(
                result.StageRun.Status is StageRunStatus.Completed
                    or StageRunStatus.PartiallyCompleted
                    or StageRunStatus.Skipped,
                $"Unexpected stage status {result.StageRun.Status}");

            if (segment.Status is LipSynthesisSegmentStatus.Synthesized)
            {
                Assert.NotNull(segment.PatchedClipRelativePath);
                Assert.Equal(LipSynthesisIntegrationSupport.LatentSyncModelId, segment.ModelId);

                ProjectArtifact artifact = Assert.Single(
                    mediaRepo.Artifacts,
                    artifact => artifact.Kind == ArtifactKind.LipSynthesisTake);
                Assert.True(
                    File.Exists(artifactStore.GetPath(artifact.RelativePath)),
                    "Registered LipSynthesisTake artifact file does not exist on disk.");
            }
            else
            {
                Assert.DoesNotContain(mediaRepo.Artifacts, artifact => artifact.Kind == ArtifactKind.LipSynthesisTake);
                Assert.False(string.IsNullOrWhiteSpace(segment.SkipReason));
            }

            Assert.True(File.Exists(videoPath), "Source video fixture must remain untouched on disk.");
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }
}
