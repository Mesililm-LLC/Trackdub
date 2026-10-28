using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.Transcript;
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
}
