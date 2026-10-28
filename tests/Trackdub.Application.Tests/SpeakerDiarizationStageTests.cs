using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class SpeakerDiarizationStageTests
{
    private readonly FakeArtifactStore artifactStore = new();
    private readonly FakeFileFingerprintService fingerprintService = new();
    private readonly FakeMediaAssetRepository mediaAssetRepository = new();
    private readonly FakeSpeakerRepository speakerRepository = new();
    private readonly FakeProjectStageRunStore stageRunStore = new();
    private readonly FakeDiarizationEngine diarizationEngine = new();
    private readonly FakeTranscriptRepository transcriptRepository = new();
    private readonly FakeTtsTakeRepository ttsTakeRepository = new();
    private readonly FakeVoiceAssignmentRepository voiceAssignmentRepository = new();
    private readonly FakeReferenceClipAnalyzer referenceClipAnalyzer = new();
    private readonly FakeReferenceClipTrimmer referenceClipTrimmer = new();

    private readonly Guid projectId = Guid.NewGuid();
    private readonly Guid mediaAssetId = Guid.NewGuid();

    private SpeakerReferenceClipService BuildReferenceClipService() =>
        new SpeakerReferenceClipService(
            artifactStore,
            new FakeAudioClipExtractor(),
            fingerprintService,
            mediaAssetRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            referenceClipAnalyzer,
            referenceClipTrimmer);

    private SpeakerAssignmentService BuildSpeakerAssignmentService() =>
        new SpeakerAssignmentService(
            speakerRepository,
            transcriptRepository,
            new SegmentEditingService(transcriptRepository, ttsTakeRepository, BuildArtifactWriter()),
            artifactStore,
            stageRunStore,
            diarizationEngine,
            BuildReferenceClipService(),
            BuildArtifactWriter(),
            new DiarizationStageHandler(
                diarizationEngine,
                new WritingModelDownloader(),
                modelCacheRoot: Path.Join(Path.GetTempPath(), "trackdub-tests", Guid.NewGuid().ToString("N")),
                expectedSha256: SortFormerTestFixtures.ExpectedSha256));

    private TranscriptArtifactWriter BuildArtifactWriter() =>
        new TranscriptArtifactWriter(artifactStore, fingerprintService, mediaAssetRepository);

    private TranscriptProjectState BuildRerunDiarizationState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Test Project", now, now);
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "media/normalized_audio.wav",
            "normalized_audio.wav",
            "abc123",
            1024L,
            now,
            "wav",
            DurationSeconds: 30.0,
            HasAudio: true,
            HasVideo: false,
            now);
        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            "media/normalized_audio.wav",
            "sha256hash",
            1024L,
            DurationSeconds: 30.0,
            SampleRate: 16000,
            ChannelCount: 1,
            now);
        artifactStore.Seed("media/normalized_audio.wav");
        mediaAssetRepository.SaveArtifactAsync(audioArtifact, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

        TranscriptRevision revision = TranscriptRevision.Create(projectId, null, 1, now);
        Guid speakerId = Guid.NewGuid();
        TranscriptSegment segment = TranscriptSegment.Create(
            revision.Id,
            segmentIndex: 0,
            startSeconds: 0d,
            endSeconds: 12d,
            text: "Hello world",
            speakerId,
            detectedLanguage: "en");
        transcriptRepository.Seed(revision, [segment]);

        var openResult = new OpenProjectResult(
            project,
            mediaAsset,
            SourceReference: null,
            SourceMediaStatus.Available,
            SourceStatusMessage: null,
            Artifacts: [audioArtifact],
            TranscriptLanguage: "en");

        return new TranscriptProjectState(
            openResult,
            revision,
            [segment],
            Speakers: [new ProjectSpeaker(speakerId, projectId, "Speaker 1", now)],
            SpeakerTurns: [],
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: "en",
            StageRuns: [],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }

    private TranscriptGenerationContext BuildContext(bool enableDiarization)
    {
        var project = new TrackdubProject(projectId, "Test Project", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "media/source.mp4",
            "source.mp4",
            "abc123",
            1024L,
            DateTimeOffset.UtcNow,
            "mp4",
            DurationSeconds: 30.0,
            HasAudio: true,
            HasVideo: true,
            DateTimeOffset.UtcNow);

        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            "media/normalized_audio.wav",
            "sha256hash",
            1024L,
            DurationSeconds: 30.0,
            SampleRate: 16000,
            ChannelCount: 1,
            DateTimeOffset.UtcNow);

        // Seed the artifact path so artifactStore.GetPath returns a usable value
        artifactStore.Seed("media/normalized_audio.wav");

        TranscriptAudioRoutingPlan routingPlan = TranscriptAudioRoutingPlan.Raw(
            audioArtifact,
            SpeechAudioSourceKind.FullMix);

        var context = new TranscriptGenerationContext(
            project,
            mediaAsset,
            audioArtifact,
            routingPlan,
            enableDiarization,
            sourceLanguage: null);

        return context with { SpeechRegions = [new SpeechRegion(0, 0.0, 30.0)] };
    }

    [Fact]
    public void SpeakerAssignmentAndPersistenceStage_UsesDistinctStageName()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerAssignmentAndPersistenceStage(
            speakerAssignmentService,
            transcriptRepository,
            artifactWriter,
            artifactStore,
            stageRunStore);

        Assert.Equal("speaker-assignment", stage.StageName);
    }

    [Fact]
    public void SpeakerAssignmentService_RequiresDiarizationStageHandler()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new SpeakerAssignmentService(
                speakerRepository,
                transcriptRepository,
                new SegmentEditingService(transcriptRepository, ttsTakeRepository, BuildArtifactWriter()),
                artifactStore,
                stageRunStore,
                diarizationEngine,
                BuildReferenceClipService(),
                BuildArtifactWriter(),
                diarizationStageHandler: null!));
    }

    [Fact]
    public async Task RerunDiarizationAsync_writes_diarization_artifact_and_reassigns_speakers()
    {
        _ = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        TranscriptProjectState state = BuildRerunDiarizationState();
        var request = new RerunDiarizationRequest();

        await speakerAssignmentService.RerunDiarizationAsync(state, request, TestContext.Current.CancellationToken);

        // Diarization artifacts are now written to run-scoped paths (atomic pointer swap via DB).
        ProjectArtifact diarizationArtifact = Assert.Single(
            mediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.DiarizationResult);
        Assert.StartsWith("pipeline/diarization-result-", diarizationArtifact.RelativePath, StringComparison.Ordinal);
        Assert.True(
            artifactStore.Blobs.ContainsKey(diarizationArtifact.RelativePath),
            $"Expected blob at '{diarizationArtifact.RelativePath}' but it was not written.");
        IReadOnlyList<TranscriptSegment> revisedSegments = await transcriptRepository.GetSegmentsAsync(
            state.CurrentTranscriptRevision!.Id,
            TestContext.Current.CancellationToken);
        Assert.All(revisedSegments, segment => Assert.NotEqual(Guid.Empty, segment.SpeakerId));
    }

    [Theory]
    [InlineData(SpeakerAssignmentOrigin.Unknown, true)]
    [InlineData(SpeakerAssignmentOrigin.Manual, true)]
    [InlineData(SpeakerAssignmentOrigin.Automatic, false)]
    public async Task RerunDiarizationAsync_preserves_protected_assignments(
        SpeakerAssignmentOrigin origin,
        bool expectedPreserved)
    {
        TranscriptProjectState state = BuildRerunDiarizationState();
        TranscriptSegment segment = Assert.Single(state.TranscriptSegments) with
        {
            SpeakerAssignmentOrigin = origin,
        };
        state = state with { TranscriptSegments = [segment] };
        speakerRepository.Seed(state.Speakers,
        [
            SpeakerTurn.Create(projectId, segment.SpeakerId!.Value, 0, 12)
        ]);

        await BuildSpeakerAssignmentService().RerunDiarizationAsync(
            state, new RerunDiarizationRequest(), TestContext.Current.CancellationToken);

        TranscriptRevision revision = (await transcriptRepository.GetCurrentRevisionAsync(
            projectId, TestContext.Current.CancellationToken))!;
        TranscriptSegment revised = Assert.Single(await transcriptRepository.GetSegmentsAsync(
            revision.Id, TestContext.Current.CancellationToken));
        Assert.Equal(expectedPreserved, revised.SpeakerId == segment.SpeakerId);
        Assert.Equal(origin, revised.SpeakerAssignmentOrigin);
        Assert.Contains(speakerRepository.Speakers, speaker => speaker.Id == segment.SpeakerId);
    }

    [Fact]
    public async Task Split_and_merge_keep_manual_speaker_origin()
    {
        TranscriptProjectState state = BuildRerunDiarizationState();
        TranscriptSegment segment = Assert.Single(state.TranscriptSegments) with
        {
            SpeakerAssignmentOrigin = SpeakerAssignmentOrigin.Manual,
        };
        state = state with { TranscriptSegments = [segment] };
        var editor = new SegmentEditingService(transcriptRepository, ttsTakeRepository, BuildArtifactWriter());
        await editor.SplitSegmentAsync(state,
            new SplitTranscriptSegmentRequest(state.CurrentTranscriptRevision!.Id, segment.Id, 6),
            TestContext.Current.CancellationToken);

        TranscriptRevision splitRevision = (await transcriptRepository.GetCurrentRevisionAsync(
            projectId, TestContext.Current.CancellationToken))!;
        IReadOnlyList<TranscriptSegment> split = await transcriptRepository.GetSegmentsAsync(
            splitRevision.Id, TestContext.Current.CancellationToken);
        Assert.Equal(2, split.Count);
        Assert.All(split, item => Assert.Equal(SpeakerAssignmentOrigin.Manual, item.SpeakerAssignmentOrigin));

        state = state with { CurrentTranscriptRevision = splitRevision, TranscriptSegments = split };
        await editor.MergeSegmentRunAsync(state,
            new MergeTranscriptSegmentRunRequest(splitRevision.Id, split.Select(item => item.Id).ToArray()),
            TestContext.Current.CancellationToken);
        TranscriptRevision mergedRevision = (await transcriptRepository.GetCurrentRevisionAsync(
            projectId, TestContext.Current.CancellationToken))!;
        TranscriptSegment merged = Assert.Single(await transcriptRepository.GetSegmentsAsync(
            mergedRevision.Id, TestContext.Current.CancellationToken));
        Assert.Equal(SpeakerAssignmentOrigin.Manual, merged.SpeakerAssignmentOrigin);
        Assert.Equal(segment.SpeakerId, merged.SpeakerId);
    }

    [Fact]
    public async Task RerunDiarizationAsync_preserves_a_manual_unassignment()
    {
        TranscriptProjectState state = BuildRerunDiarizationState();
        TranscriptSegment segment = Assert.Single(state.TranscriptSegments) with
        {
            SpeakerId = null,
            SpeakerAssignmentOrigin = SpeakerAssignmentOrigin.Manual,
        };
        state = state with { TranscriptSegments = [segment] };

        await BuildSpeakerAssignmentService().RerunDiarizationAsync(
            state, new RerunDiarizationRequest(), TestContext.Current.CancellationToken);

        TranscriptRevision revision = (await transcriptRepository.GetCurrentRevisionAsync(
            projectId, TestContext.Current.CancellationToken))!;
        TranscriptSegment revised = Assert.Single(await transcriptRepository.GetSegmentsAsync(
            revision.Id, TestContext.Current.CancellationToken));
        Assert.Null(revised.SpeakerId);
        Assert.Equal(SpeakerAssignmentOrigin.Manual, revised.SpeakerAssignmentOrigin);
    }

    [Fact]
    public async Task ExecuteAsync_WritesDiarizationArtifact_WhenDiarizationIsEnabled()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = BuildContext(enableDiarization: true);
        await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        // Diarization writes use run-scoped paths so a partial write never overlaps a prior
        // run's stable file. Validate the relative path shape and that the file is present.
        ProjectArtifact artifact = Assert.Single(
            mediaAssetRepository.Artifacts,
            a => a.Kind == ArtifactKind.DiarizationResult);
        Assert.StartsWith("pipeline/diarization-result-", artifact.RelativePath, StringComparison.Ordinal);
        Assert.True(
            artifactStore.Blobs.ContainsKey(artifact.RelativePath),
            $"Expected blob at '{artifact.RelativePath}' but it was not written.");
        Assert.Equal(projectId, artifact.ProjectId);
        Assert.Equal(mediaAssetId, artifact.MediaAssetId);
        Assert.Equal("generated-diarization", artifact.Provenance);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotWriteDiarizationArtifact_WhenDiarizationIsDisabled()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = BuildContext(enableDiarization: false);
        await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        // No diarization artifact should be written
        Assert.DoesNotContain(
            artifactStore.Blobs.Keys,
            key => key.Contains("diarization", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(
            mediaAssetRepository.Artifacts,
            a => a.Kind == ArtifactKind.DiarizationResult);
    }

    [Fact]
    public async Task ExecuteAsync_SetsDiarizationResult_WhenDiarizationIsEnabled()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = BuildContext(enableDiarization: true);
        context = await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(context.DiarizationResult);
        Assert.NotEmpty(context.DiarizationResult.Speakers);
        Assert.NotEmpty(context.DiarizationResult.Turns);
    }

    [Fact]
    public async Task ExecuteAsync_LeavesDiarizationResultNull_WhenDiarizationIsDisabled()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = BuildContext(enableDiarization: false);
        context = await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Null(context.DiarizationResult);
    }

    [Fact]
    public async Task ExecuteAsync_WhenVadHasNoRegions_SkipsDiarizationAndBuildsAnEmptyPlan()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext result = await stage.ExecuteAsync(
            BuildContext(enableDiarization: true) with { SpeechRegions = [] },
            TestContext.Current.CancellationToken);

        Assert.Null(result.DiarizationResult);
        Assert.NotNull(result.RegionPlan);
        Assert.Empty(result.RegionPlan.Regions);
        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Skipped, stageRun.Status);
        Assert.Equal(StageSkipReasonCodes.NoSpeechRegions, stageRun.FailureReason);
        Assert.DoesNotContain(mediaAssetRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.DiarizationResult);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDiarizationDisabled_DoesNotCreateStageRun()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        await stage.ExecuteAsync(BuildContext(enableDiarization: false), TestContext.Current.CancellationToken);

        Assert.Empty(stageRunStore.All);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDiarizationEnabled_CompletesDiarizationStageRun()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        await stage.ExecuteAsync(BuildContext(enableDiarization: true), TestContext.Current.CancellationToken);

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDiarizationEnabled_AttachesStageRunIdToTurns()
    {
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = await stage.ExecuteAsync(
            BuildContext(enableDiarization: true),
            TestContext.Current.CancellationToken);

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.NotNull(context.DiarizationResult);
        Assert.All(context.DiarizationResult.Turns, turn => Assert.Equal(stageRun.Id, turn.StageRunId));
    }

    [Fact]
    public async Task ExecuteAsync_WhenEngineFails_RecordsFailedDiarizationStageRun()
    {
        diarizationEngine.ThrowOnDiarize = true;
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = await stage.ExecuteAsync(
            BuildContext(enableDiarization: true),
            TestContext.Current.CancellationToken);

        Assert.Null(context.DiarizationResult);
        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Failed, stageRun.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceled_RecordsCanceledDiarizationStageRun()
    {
        using var cancellation = new CancellationTokenSource();
        diarizationEngine.ExceptionToThrow = new OperationCanceledException(cancellation.Token);
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            stage.ExecuteAsync(BuildContext(enableDiarization: true), cancellation.Token));

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Diarization, stageRun.StageName);
        Assert.Equal(StageRunStatus.Canceled, stageRun.Status);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEngineReturnsNoTurns_CompletesStageRunWithoutDiarizationArtifact()
    {
        diarizationEngine.OverrideTurns = [];
        TranscriptArtifactWriter artifactWriter = BuildArtifactWriter();
        SpeakerAssignmentService speakerAssignmentService = BuildSpeakerAssignmentService();
        var stage = new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore);

        TranscriptGenerationContext context = await stage.ExecuteAsync(
            BuildContext(enableDiarization: true),
            TestContext.Current.CancellationToken);

        Assert.Null(context.DiarizationResult);
        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
        Assert.DoesNotContain(
            mediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.DiarizationResult);
    }

    private sealed class FakeAudioClipExtractor : IAudioClipExtractor
    {
        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            double startSeconds,
            double endSeconds,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioClipExtractionResult(destinationPath, 0.0, 16000, 1));

        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            IReadOnlyList<AudioClipRange> ranges,
            string destinationPath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AudioClipExtractionResult(destinationPath, 0.0, 16000, 1));
    }

    private sealed class WritingModelDownloader : IModelDownloaderContract
    {
        public async Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, SortFormerTestFixtures.ModelBytes, cancellationToken);
            return true;
        }

        public Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
