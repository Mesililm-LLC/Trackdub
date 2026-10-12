using Trackdub.Application.Dubbing;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// TTS overlapping streamed translation: clips rendered while translation runs are published by
/// the TTS stage through the normal take commit instead of being synthesized again.
/// </summary>
public sealed class TtsStreamingPrefetchTests
{
    [Fact]
    public async Task HandleAsync_WithClipsRenderedDuringTranslation_PublishesTakesWithoutResynthesizing()
    {
        var engine = new FakeTtsEngine();
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(engine, stageRunStore);
        StartTtsStageRequest request = CreateRequest(segmentCount: 3);
        await using TtsStreamingPrefetch prefetch = await CreatePrefetchAsync(handler, request);

        foreach (TranslatedSegment segment in request.TranslatedSegments)
        {
            prefetch.OnSegmentTranslated(segment.SegmentIndex, segment.Text);
        }

        StartTtsStageResult result = await handler.HandleAsync(
            request, TestContext.Current.CancellationToken, progress: null, prefetch);

        Assert.Equal(StageRunStatus.Completed, Assert.Single(stageRunStore.All).Status);
        Assert.Equal(3, result.Takes.Count);
        Assert.All(result.Takes, take => Assert.NotNull(take.ArtifactId));
        Assert.Equal(3, engine.SynthesizeCallCount);
        Assert.Equal(3, prefetch.ScheduledCount);
        Assert.Equal(3, prefetch.ClaimedCount);
        Assert.Equal(0, prefetch.MissedCount);
    }

    [Fact]
    public async Task HandleAsync_WhenCommittedTextDiffersFromRenderedText_SynthesizesTheCommittedText()
    {
        const string draftText = "a draft the committed revision does not contain";
        var engine = new SignalingTtsEngine(draftText);
        var stageRunStore = new FakeProjectStageRunStore();
        using var handler = CreateHandler(engine, stageRunStore);
        StartTtsStageRequest request = CreateRequest(segmentCount: 2);
        await using TtsStreamingPrefetch prefetch = await CreatePrefetchAsync(handler, request);

        prefetch.OnSegmentTranslated(0, draftText);
        prefetch.OnSegmentTranslated(1, request.TranslatedSegments[1].Text);

        StartTtsStageResult result = await handler.HandleAsync(
            request, TestContext.Current.CancellationToken, progress: null, prefetch);

        // The unclaimed draft render runs in the background and the stage never awaits it, so wait
        // for the engine to finish it before counting synthesize calls.
        await engine.SignaledText.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Takes.Count);
        Assert.Equal(1, prefetch.ClaimedCount);
        Assert.Equal(1, prefetch.MissedCount);
        // Two prefetch renders plus one stage render of the committed segment 0 text.
        Assert.Equal(3, engine.SynthesizeCallCount);
        // Prefetch renders finish in any order, so the take's text hash (not the engine's last
        // input) proves the committed text was synthesized.
        TtsTake segmentZero = Assert.Single(result.Takes, take => take.SegmentIndex == 0);
        Assert.Equal(
            TtsTextHash.Compute(0, request.TranslatedSegments[0].Text),
            segmentZero.TranslatedTextHash);
    }

    [Fact]
    public async Task DisposeAsync_DeletesAudioTheStageNeverClaimed()
    {
        var engine = new FakeTtsEngine();
        using var handler = CreateHandler(engine, new FakeProjectStageRunStore());
        StartTtsStageRequest request = CreateRequest(segmentCount: 2);
        TtsStreamingPrefetch prefetch = await CreatePrefetchAsync(handler, request);
        string stagingDirectory = prefetch.StagingDirectory;
        prefetch.OnSegmentTranslated(0, request.TranslatedSegments[0].Text);
        prefetch.OnSegmentTranslated(1, request.TranslatedSegments[1].Text);
        Assert.True(Directory.Exists(stagingDirectory));

        await prefetch.DisposeAsync();

        Assert.False(Directory.Exists(stagingDirectory));
    }

    [Theory]
    [InlineData(true, new[] { "asr", "translation", "tts" }, 1, true)]
    [InlineData(false, new[] { "asr", "translation", "tts" }, 1, false)]
    [InlineData(true, new[] { "asr", "translation", "export" }, 1, false)]
    [InlineData(true, new[] { "asr", "translation", "tts" }, 0, false)]
    public void ShouldOverlapTtsWithTranslation_RequiresStreamingAndALaterTtsStage(
        bool streaming,
        string[] stages,
        int index,
        bool expected)
    {
        var options = new DubbingSessionOptions { SourceMediaPath = "source.mp4", TargetLanguageCode = "es", EnableTranslationSegmentStreaming = streaming };

        Assert.Equal(
            expected,
            DubbingPipelineEngine.ShouldOverlapTtsWithTranslation(options, stages, index, new HashSet<string>()));
    }

    [Fact]
    public void ShouldOverlapTtsWithTranslation_DeclinedTtsStage_DoesNotOverlap()
    {
        var options = new DubbingSessionOptions { SourceMediaPath = "source.mp4", TargetLanguageCode = "es", EnableTranslationSegmentStreaming = true };

        Assert.False(DubbingPipelineEngine.ShouldOverlapTtsWithTranslation(
            options,
            ["translation", "tts"],
            0,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "tts" }));
    }

    private sealed class SignalingTtsEngine(string signalText) : FakeTtsEngine
    {
        private readonly TaskCompletionSource signaled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task SignaledText => signaled.Task;

        public override async Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            TtsSynthesisResult result = await base.SynthesizeAsync(request, cancellationToken);
            if (string.Equals(request.Text, signalText, StringComparison.Ordinal))
            {
                signaled.TrySetResult();
            }

            return result;
        }
    }

    private static async Task<TtsStreamingPrefetch> CreatePrefetchAsync(
        StartTtsStageHandler handler,
        StartTtsStageRequest request)
    {
        (TtsPrefetchSpeaker Speaker, int MaxConcurrency) prepared = (await handler.PreparePrefetchSpeakerAsync(
            request with { TranslatedSegments = [] },
            TestContext.Current.CancellationToken)).GetValueOrDefault();
        Assert.NotNull(prepared.Speaker);
        Dictionary<int, TtsPrefetchSpeaker> speakersByIndex = prepared.Speaker.SourceSegmentsByIndex.Keys
            .ToDictionary(index => index, _ => prepared.Speaker);
        return new TtsStreamingPrefetch(
            handler,
            speakersByIndex,
            new Dictionary<Guid, TtsSpeakerPlan>(),
            maxConcurrency: 1,
            logger: null,
            TestContext.Current.CancellationToken);
    }

    private static StartTtsStageHandler CreateHandler(FakeTtsEngine engine, FakeProjectStageRunStore stageRunStore)
    {
        var artifactStore = new FakeArtifactStore();
        var mediaRepository = new FakeMediaAssetRepository();
        var takeRepository = new FakeTtsTakeRepository();
        return new StartTtsStageHandler(
            engine,
            new FakeVoiceCatalog(),
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, DateTimeOffset.UtcNow)),
            mediaRepository,
            takeRepository,
            stageRunStore,
            logger: null,
            commitBoundary: TestAtomicCommitBoundary.Create(null, takeRepository, artifactStore, mediaRepository),
            executionOptions: new TtsExecutionOptions(1, 0));
    }

    private static StartTtsStageRequest CreateRequest(int segmentCount)
    {
        Guid projectId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            segmentCount + 1.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        TranscriptSegment[] transcriptSegments = Enumerable.Range(0, segmentCount)
            .Select(index => TranscriptSegment.Create(
                Guid.NewGuid(),
                index,
                index,
                index + 1.0d,
                $"Line {index}.",
                speakerId,
                "en"))
            .ToArray();
        Guid translationRevisionId = Guid.NewGuid();
        TranslatedSegment[] translatedSegments = transcriptSegments
            .Select(segment => TranslatedSegment.Create(
                translationRevisionId,
                segment.SegmentIndex,
                segment.StartSeconds,
                segment.EndSeconds,
                $"ES {segment.SegmentIndex}"))
            .ToArray();

        return new StartTtsStageRequest(
            projectId,
            mediaAsset,
            speakerId,
            "es",
            VoiceAssignment.Create(projectId, speakerId, "af_heart"),
            transcriptSegments,
            translatedSegments);
    }
}
