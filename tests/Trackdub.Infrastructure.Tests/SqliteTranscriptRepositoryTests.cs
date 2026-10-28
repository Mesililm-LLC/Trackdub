using Trackdub.Domain;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Tts;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteTranscriptRepositoryTests
{
    [Fact]
    public async Task Merging_speakers_preserves_take_history_and_target_voice()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Merge.trackdub");
        try
        {
            var db = new SqliteProjectDatabase(root);
            var projects = new SqliteProjectRepository(db);
            var speakers = new SqliteSpeakerRepository(db);
            var transcripts = new SqliteTranscriptRepository(db);
            var voices = new SqliteVoiceAssignmentRepository(db);
            var takes = new SqliteTtsTakeRepository(db);
            CancellationToken ct = TestContext.Current.CancellationToken;
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Merge", now, now);
            await projects.InitializeAsync(project, ct);
            var source = await speakers.EnsureDefaultSpeakerAsync(project.Id, ct);
            var target = await speakers.CreateSpeakerAsync(project.Id, ct);
            var sourceVoice = VoiceAssignment.Create(project.Id, source.Id, "source-model", "source");
            var targetVoice = VoiceAssignment.Create(project.Id, target.Id, "target-model", "target");
            await voices.SaveAsync(sourceVoice, ct);
            await voices.SaveAsync(targetVoice, ct);
            var revision = TranscriptRevision.Create(project.Id, null, 1, now);
            await transcripts.SaveRevisionAsync(revision,
            [
                TranscriptSegment.Create(revision.Id, 0, 0, 1, "Source", source.Id),
                TranscriptSegment.Create(revision.Id, 1, 1, 2, "Target", target.Id),
            ], ct);
            var sourceTake = TtsTake.Create(project.Id, sourceVoice.Id, segmentIndex: 0);
            var targetTake = TtsTake.Create(project.Id, targetVoice.Id, segmentIndex: 1);
            var historicalTake = TtsTake.Create(project.Id, sourceVoice.Id, segmentIndex: 8);
            await takes.SaveAsync(sourceTake, ct);
            await takes.SaveAsync(targetTake, ct);
            await takes.SaveAsync(historicalTake, ct);

            await Assert.ThrowsAsync<InvalidOperationException>(() => transcripts.ReassignAndMergeSpeakersAsync(project.Id, source.Id, Guid.NewGuid(), ct));
            Assert.False((await takes.GetAsync(sourceTake.Id, ct))!.IsStale);
            await transcripts.ReassignAndMergeSpeakersAsync(project.Id, source.Id, target.Id, ct);

            Assert.Equal(target.Id, Assert.Single(await speakers.ListSpeakersAsync(project.Id, ct)).Id);
            Assert.Equal(targetVoice.Id, Assert.Single(await voices.GetAllAsync(project.Id, ct)).Id);
            Assert.Null(await voices.GetAsync(project.Id, source.Id, ct));
            Assert.Equal(targetVoice.Id, (await voices.GetAsync(project.Id, target.Id, ct))!.Id);
            TtsTake savedSource = (await takes.GetAsync(sourceTake.Id, ct))!;
            Assert.Equal(sourceVoice.Id, savedSource.VoiceAssignmentId);
            Assert.Equal("source-model", (await voices.GetByIdAsync(project.Id, savedSource.VoiceAssignmentId, ct))!.VoiceModelId);
            Assert.Null(await voices.GetByIdAsync(Guid.NewGuid(), savedSource.VoiceAssignmentId, ct));
            Assert.True(savedSource.IsStale);
            Assert.False((await takes.GetAsync(targetTake.Id, ct))!.IsStale);
            Assert.True((await takes.GetAsync(historicalTake.Id, ct))!.IsStale);
            Assert.Equal(3, (await takes.GetByProjectAsync(project.Id, ct)).Count);
            var segments = await transcripts.GetSegmentsAsync(revision.Id, ct);
            Assert.All(segments, segment => Assert.Equal(target.Id, segment.SpeakerId));
            Assert.Equal(SpeakerAssignmentOrigin.Manual, segments[0].SpeakerAssignmentOrigin);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Revision_changes_invalidate_only_affected_takes_in_the_revision_transaction()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Transcript.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projects = new SqliteProjectRepository(database);
            var transcripts = new SqliteTranscriptRepository(database);
            var speakers = new SqliteSpeakerRepository(database);
            var voices = new SqliteVoiceAssignmentRepository(database);
            var takes = new SqliteTtsTakeRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Transcript", now, now);
            CancellationToken ct = TestContext.Current.CancellationToken;
            await projects.InitializeAsync(project, ct);
            var firstSpeaker = await speakers.EnsureDefaultSpeakerAsync(project.Id, ct);
            var secondSpeaker = await speakers.CreateSpeakerAsync(project.Id, ct);
            VoiceAssignment voice = VoiceAssignment.Create(project.Id, firstSpeaker.Id, "model", "voice");
            await voices.SaveAsync(voice, ct);
            TranscriptRevision first = TranscriptRevision.Create(project.Id, null, 1, now);
            await transcripts.SaveRevisionAsync(first,
            [
                TranscriptSegment.Create(first.Id, 0, 0, 1, "One", firstSpeaker.Id),
                TranscriptSegment.Create(first.Id, 1, 1, 2, "Two", firstSpeaker.Id),
                TranscriptSegment.Create(first.Id, 2, 2, 3, "Three", firstSpeaker.Id),
                TranscriptSegment.Create(first.Id, 3, 3, 4, "Four", firstSpeaker.Id),
            ], ct);
            for (int index = 0; index < 4; index++)
            {
                await takes.SaveAsync(TtsTake.Create(project.Id, voice.Id, segmentIndex: index), ct);
            }

            TranscriptRevision second = TranscriptRevision.Create(project.Id, null, 2, now.AddSeconds(1));
            await transcripts.SaveRevisionAsync(second,
            [
                TranscriptSegment.Create(second.Id, 0, 0, 1, "One", secondSpeaker.Id),
                TranscriptSegment.Create(second.Id, 1, 1, 2, "Two", firstSpeaker.Id),
                TranscriptSegment.Create(second.Id, 2, 2, 2.5, "Three", firstSpeaker.Id),
            ], ct);

            var savedTakes = (await takes.GetByProjectAsync(project.Id, ct)).ToDictionary(take => take.SegmentIndex);
            Assert.True(savedTakes[0].IsStale);
            Assert.Equal(TtsTakeStatus.Stale, savedTakes[0].Status);
            Assert.False(savedTakes[1].IsStale);
            Assert.True(savedTakes[2].IsStale);
            Assert.True(savedTakes[3].IsStale);

            await transcripts.ReassignSpeakerAsync(project.Id, firstSpeaker.Id, secondSpeaker.Id, ct);
            savedTakes = (await takes.GetByProjectAsync(project.Id, ct)).ToDictionary(take => take.SegmentIndex);
            Assert.True(savedTakes[1].IsStale);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Repository_round_trips_current_revision_segments_and_stage_runs()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Transcript.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Transcript", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            var speaker = await speakerRepository.EnsureDefaultSpeakerAsync(project.Id, TestContext.Current.CancellationToken);

            StageRunRecord asrStageRun = StageRunRecord.Start(project.Id, "asr", now)
                .WithRuntimeInfo("auto", "cpu", "onnx-community/whisper-tiny", "whisper-tiny-onnx", "int8", "bootstrap skipped")
                .Complete(now.AddSeconds(2));
            await stageRunStore.CreateAsync(asrStageRun, TestContext.Current.CancellationToken);
            await stageRunStore.UpdateAsync(asrStageRun, TestContext.Current.CancellationToken);

            TranscriptRevision revision = TranscriptRevision.Create(project.Id, asrStageRun.Id, revisionNumber: 1, now.AddSeconds(3));
            TranscriptSegment[] segments =
            [
                TranscriptSegment.Create(
                    revision.Id,
                    0,
                    0.0,
                    1.5,
                    "Hello",
                    speakerId: speaker.Id,
                    detectedLanguage: "en",
                    words:
                    [
                        TranscriptWord.Create(0, 0.0, 0.7, "Hel", 0.62d),
                        TranscriptWord.Create(1, 0.7, 1.5, "lo", 0.91d)
                    ],
                    speakerAssignmentOrigin: SpeakerAssignmentOrigin.Manual),
                TranscriptSegment.Create(revision.Id, 1, 1.5, 3.0, "World", detectedLanguage: "en")
            ];

            await transcriptRepository.SaveRevisionAsync(revision, segments, TestContext.Current.CancellationToken);

            TranscriptRevision? current = await transcriptRepository.GetCurrentRevisionAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<TranscriptSegment> reloadedSegments = await transcriptRepository.GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<StageRunRecord> stageRuns = await stageRunStore.ListByProjectAsync(project.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(current);
            Assert.Equal(asrStageRun.Id, current!.StageRunId);
            Assert.Equal(2, reloadedSegments.Count);
            Assert.Equal("Hello", reloadedSegments[0].Text);
            Assert.Equal("en", reloadedSegments[0].DetectedLanguage);
            Assert.Equal(speaker.Id, reloadedSegments[0].SpeakerId);
            Assert.Equal(SpeakerAssignmentOrigin.Manual, reloadedSegments[0].SpeakerAssignmentOrigin);
            Assert.Equal(SpeakerAssignmentOrigin.Unknown, reloadedSegments[1].SpeakerAssignmentOrigin);
            Assert.Equal(2, reloadedSegments[0].Words.Count);
            Assert.Equal("Hel", reloadedSegments[0].Words[0].Text);
            Assert.Equal(0.62d, reloadedSegments[0].Words[0].Confidence);
            Assert.Empty(reloadedSegments[1].Words);
            Assert.Single(stageRuns);
            Assert.Equal(StageRunStatus.Completed, stageRuns[0].Status);
            Assert.NotNull(stageRuns[0].RuntimeInfo);
            Assert.Equal("cpu", stageRuns[0].RuntimeInfo!.SelectedProvider);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
