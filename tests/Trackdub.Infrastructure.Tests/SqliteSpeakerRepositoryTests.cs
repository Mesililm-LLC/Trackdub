using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Tts;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteSpeakerRepositoryTests
{
    [Fact]
    public async Task ReplaceDiarizationAsync_preserves_assigned_default_speaker_when_retrying_no_turn_diarization()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker defaultSpeaker = await speakerRepository.EnsureDefaultSpeakerAsync(project.Id, TestContext.Current.CancellationToken);

            TranscriptRevision revision = TranscriptRevision.Create(project.Id, stageRunId: null, revisionNumber: 1, now);
            TranscriptSegment[] segments =
            [
                TranscriptSegment.Create(revision.Id, 0, 0d, 5.8d, "Hello", defaultSpeaker.Id)
            ];
            await transcriptRepository.SaveRevisionAsync(revision, segments, TestContext.Current.CancellationToken);

            ProjectSpeaker speaker2 = ProjectSpeaker.Create(project.Id, "Speaker 2", now.AddMilliseconds(1));
            ProjectSpeaker speaker3 = ProjectSpeaker.Create(project.Id, "Speaker 3", now.AddMilliseconds(2));
            SpeakerTurn[] turns =
            [
                SpeakerTurn.Create(project.Id, speaker2.Id, 0d, 5.8d, 0.9d, hasOverlap: false),
                SpeakerTurn.Create(project.Id, speaker3.Id, 6d, 11.8d, 0.8d, hasOverlap: false)
            ];

            await speakerRepository.ReplaceDiarizationAsync(project.Id, [speaker2, speaker3], turns, TestContext.Current.CancellationToken);

            IReadOnlyList<ProjectSpeaker> speakers = await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<SpeakerTurn> reloadedTurns = await speakerRepository.ListTurnsAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<TranscriptSegment> reloadedSegments = await transcriptRepository.GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);

            Assert.Equal(3, speakers.Count);
            Assert.Contains(speakers, speaker => speaker.Id == defaultSpeaker.Id);
            Assert.Contains(speakers, speaker => speaker.Id == speaker2.Id);
            Assert.Contains(speakers, speaker => speaker.Id == speaker3.Id);
            Assert.Equal(2, reloadedTurns.Count);
            Assert.Equal(defaultSpeaker.Id, Assert.Single(reloadedSegments).SpeakerId);

            ProjectSpeaker speaker4 = ProjectSpeaker.Create(project.Id, "Speaker 4", now.AddMilliseconds(3));
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [speaker4],
                [SpeakerTurn.Create(project.Id, speaker4.Id, 0d, 5.8d)],
                TestContext.Current.CancellationToken);
            speakers = await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken);
            reloadedTurns = await speakerRepository.ListTurnsAsync(project.Id, TestContext.Current.CancellationToken);
            reloadedSegments = await transcriptRepository.GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
            Assert.Contains(speakers, speaker => speaker.Id == defaultSpeaker.Id);
            Assert.Contains(speakers, speaker => speaker.Id == speaker4.Id);
            Assert.Equal(speaker4.Id, Assert.Single(reloadedTurns).SpeakerId);
            Assert.Equal(defaultSpeaker.Id, Assert.Single(reloadedSegments).SpeakerId);
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
    public async Task ReplaceDiarizationAsync_keeps_existing_speakers_when_a_rerun_resupplies_their_ids()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker speaker = ProjectSpeaker.Create(project.Id, "Speaker 1", now);
            SpeakerTurn[] turns = [SpeakerTurn.Create(project.Id, speaker.Id, 0d, 5.8d, 0.9d, hasOverlap: false)];

            await speakerRepository.ReplaceDiarizationAsync(project.Id, [speaker], turns, TestContext.Current.CancellationToken);

            // A cached rerun or fixture can resupply the same speaker identity.
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [speaker], turns, TestContext.Current.CancellationToken);

            IReadOnlyList<ProjectSpeaker> speakers = await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<SpeakerTurn> reloadedTurns = await speakerRepository.ListTurnsAsync(project.Id, TestContext.Current.CancellationToken);
            ProjectSpeaker onlySpeaker = Assert.Single(speakers);
            Assert.Equal(speaker.Id, onlySpeaker.Id);
            Assert.Single(reloadedTurns);
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
    public async Task ReplaceDiarizationAsync_attributes_replayed_merged_speaker_turns_to_the_active_target()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker source = ProjectSpeaker.Create(project.Id, "Speaker 1", now.AddMilliseconds(1));
            ProjectSpeaker target = ProjectSpeaker.Create(project.Id, "Speaker 2", now.AddMilliseconds(2));
            SpeakerTurn[] turns = [SpeakerTurn.Create(project.Id, source.Id, 0d, 5.8d, 0.9d, hasOverlap: false)];
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [source, target], turns, TestContext.Current.CancellationToken);
            await transcriptRepository.ReassignAndMergeSpeakersAsync(project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

            // A replay of the same diarization result must not attach turns to the archived speaker.
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [source], turns, TestContext.Current.CancellationToken);

            IReadOnlyList<SpeakerTurn> reloadedTurns = await speakerRepository.ListTurnsAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<ProjectSpeaker> speakers = await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken);
            SpeakerTurn turn = Assert.Single(reloadedTurns);
            Assert.Equal(target.Id, turn.SpeakerId);
            ProjectSpeaker activeSpeaker = Assert.Single(speakers);
            Assert.Equal(target.Id, activeSpeaker.Id);
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
    public async Task ListMergedSpeakersAsync_returns_archived_speakers_with_their_merge_target()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker source = ProjectSpeaker.Create(project.Id, "Speaker 1", now.AddMilliseconds(1));
            ProjectSpeaker target = ProjectSpeaker.Create(project.Id, "Speaker 2", now.AddMilliseconds(2));
            SpeakerTurn[] turns = [SpeakerTurn.Create(project.Id, source.Id, 0d, 5.8d, 0.9d, hasOverlap: false)];
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [source, target], turns, TestContext.Current.CancellationToken);
            await transcriptRepository.ReassignAndMergeSpeakersAsync(project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

            IReadOnlyList<MergedSpeakerEntry> merged = await speakerRepository.ListMergedSpeakersAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<ProjectSpeaker> active = await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken);

            MergedSpeakerEntry entry = Assert.Single(merged);
            Assert.Equal(source.Id, entry.Speaker.Id);
            Assert.Equal("Speaker 1", entry.Speaker.DisplayName);
            Assert.Equal(target.Id, entry.MergedIntoSpeakerId);
            Assert.DoesNotContain(active, speaker => speaker.Id == source.Id);
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
    public async Task DeleteEmptySpeakerAsync_removes_a_speaker_with_no_references()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker speaker = await speakerRepository.CreateSpeakerAsync(project.Id, TestContext.Current.CancellationToken);

            await speakerRepository.DeleteEmptySpeakerAsync(project.Id, speaker.Id, TestContext.Current.CancellationToken);

            Assert.Empty(await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken));
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
    public async Task DeleteEmptySpeakerAsync_refuses_a_speaker_with_turns()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker speaker = ProjectSpeaker.Create(project.Id, "Speaker 1", now);
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [speaker],
                [SpeakerTurn.Create(project.Id, speaker.Id, 0d, 5.8d)],
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                speakerRepository.DeleteEmptySpeakerAsync(project.Id, speaker.Id, TestContext.Current.CancellationToken));
            Assert.Single(await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken));
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
    public async Task DeleteEmptySpeakerAsync_refuses_a_speaker_with_a_voice_assignment()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var voiceAssignmentRepository = new SqliteVoiceAssignmentRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker speaker = await speakerRepository.CreateSpeakerAsync(project.Id, TestContext.Current.CancellationToken);
            await voiceAssignmentRepository.SaveAsync(
                VoiceAssignment.Create(project.Id, speaker.Id, "kokoro-onnx"),
                TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                speakerRepository.DeleteEmptySpeakerAsync(project.Id, speaker.Id, TestContext.Current.CancellationToken));
            Assert.Single(await speakerRepository.ListSpeakersAsync(project.Id, TestContext.Current.CancellationToken));
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
    public async Task DeleteEmptySpeakerAsync_refuses_a_merged_speaker()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker source = ProjectSpeaker.Create(project.Id, "Speaker 1", now.AddMilliseconds(1));
            ProjectSpeaker target = ProjectSpeaker.Create(project.Id, "Speaker 2", now.AddMilliseconds(2));
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [source, target], [], TestContext.Current.CancellationToken);
            await transcriptRepository.ReassignAndMergeSpeakersAsync(project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                speakerRepository.DeleteEmptySpeakerAsync(project.Id, source.Id, TestContext.Current.CancellationToken));
            Assert.Single(await speakerRepository.ListMergedSpeakersAsync(project.Id, TestContext.Current.CancellationToken));
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
    public async Task GetForSpeakerIncludingMergedAsync_returns_the_assignment_of_an_archived_speaker()
    {
        string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var speakerRepository = new SqliteSpeakerRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            var voiceAssignmentRepository = new SqliteVoiceAssignmentRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            ProjectSpeaker source = ProjectSpeaker.Create(project.Id, "Speaker 1", now.AddMilliseconds(1));
            ProjectSpeaker target = ProjectSpeaker.Create(project.Id, "Speaker 2", now.AddMilliseconds(2));
            await speakerRepository.ReplaceDiarizationAsync(project.Id, [source, target], [], TestContext.Current.CancellationToken);
            VoiceAssignment assignment = VoiceAssignment.Create(project.Id, source.Id, "kokoro-onnx", "af-heart");
            await voiceAssignmentRepository.SaveAsync(assignment, TestContext.Current.CancellationToken);
            await transcriptRepository.ReassignAndMergeSpeakersAsync(project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

            Assert.Null(await voiceAssignmentRepository.GetAsync(project.Id, source.Id, TestContext.Current.CancellationToken));
            VoiceAssignment? history = await voiceAssignmentRepository.GetForSpeakerIncludingMergedAsync(project.Id, source.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(history);
            Assert.Equal(assignment.Id, history.Id);
            Assert.Equal(source.Id, history.SpeakerId);
            Assert.Equal("af-heart", history.VoiceVariant);
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
