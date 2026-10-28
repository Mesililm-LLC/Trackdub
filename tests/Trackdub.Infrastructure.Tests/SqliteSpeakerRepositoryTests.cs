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
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker defaultSpeaker = await db.Speakers.EnsureDefaultSpeakerAsync(db.Project.Id, TestContext.Current.CancellationToken);

        TranscriptRevision revision = TranscriptRevision.Create(db.Project.Id, stageRunId: null, revisionNumber: 1, db.Now);
        TranscriptSegment[] segments =
        [
            TranscriptSegment.Create(revision.Id, 0, 0d, 5.8d, "Hello", defaultSpeaker.Id)
        ];
        await db.Transcripts.SaveRevisionAsync(revision, segments, TestContext.Current.CancellationToken);

        ProjectSpeaker speaker2 = ProjectSpeaker.Create(db.Project.Id, "Speaker 2", db.Now.AddMilliseconds(1));
        ProjectSpeaker speaker3 = ProjectSpeaker.Create(db.Project.Id, "Speaker 3", db.Now.AddMilliseconds(2));
        SpeakerTurn[] turns =
        [
            SpeakerTurn.Create(db.Project.Id, speaker2.Id, 0d, 5.8d, 0.9d, hasOverlap: false),
            SpeakerTurn.Create(db.Project.Id, speaker3.Id, 6d, 11.8d, 0.8d, hasOverlap: false)
        ];

        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [speaker2, speaker3], turns, TestContext.Current.CancellationToken);

        IReadOnlyList<ProjectSpeaker> speakers = await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);
        IReadOnlyList<SpeakerTurn> reloadedTurns = await db.Speakers.ListTurnsAsync(db.Project.Id, TestContext.Current.CancellationToken);
        IReadOnlyList<TranscriptSegment> reloadedSegments = await db.Transcripts.GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);

        Assert.Equal(3, speakers.Count);
        Assert.Contains(speakers, speaker => speaker.Id == defaultSpeaker.Id);
        Assert.Contains(speakers, speaker => speaker.Id == speaker2.Id);
        Assert.Contains(speakers, speaker => speaker.Id == speaker3.Id);
        Assert.Equal(2, reloadedTurns.Count);
        Assert.Equal(defaultSpeaker.Id, Assert.Single(reloadedSegments).SpeakerId);

        ProjectSpeaker speaker4 = ProjectSpeaker.Create(db.Project.Id, "Speaker 4", db.Now.AddMilliseconds(3));
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [speaker4],
            [SpeakerTurn.Create(db.Project.Id, speaker4.Id, 0d, 5.8d)],
            TestContext.Current.CancellationToken);
        speakers = await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);
        reloadedTurns = await db.Speakers.ListTurnsAsync(db.Project.Id, TestContext.Current.CancellationToken);
        reloadedSegments = await db.Transcripts.GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
        Assert.Contains(speakers, speaker => speaker.Id == defaultSpeaker.Id);
        Assert.Contains(speakers, speaker => speaker.Id == speaker4.Id);
        Assert.Equal(speaker4.Id, Assert.Single(reloadedTurns).SpeakerId);
        Assert.Equal(defaultSpeaker.Id, Assert.Single(reloadedSegments).SpeakerId);
    }

    [Fact]
    public async Task ReplaceDiarizationAsync_keeps_existing_speakers_when_a_rerun_resupplies_their_ids()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker speaker = ProjectSpeaker.Create(db.Project.Id, "Speaker 1", db.Now);
        SpeakerTurn[] turns = [SpeakerTurn.Create(db.Project.Id, speaker.Id, 0d, 5.8d, 0.9d, hasOverlap: false)];

        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [speaker], turns, TestContext.Current.CancellationToken);

        // A cached rerun or fixture can resupply the same speaker identity.
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [speaker], turns, TestContext.Current.CancellationToken);

        IReadOnlyList<ProjectSpeaker> speakers = await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);
        IReadOnlyList<SpeakerTurn> reloadedTurns = await db.Speakers.ListTurnsAsync(db.Project.Id, TestContext.Current.CancellationToken);
        ProjectSpeaker onlySpeaker = Assert.Single(speakers);
        Assert.Equal(speaker.Id, onlySpeaker.Id);
        Assert.Single(reloadedTurns);
    }

    [Fact]
    public async Task ReplaceDiarizationAsync_attributes_replayed_merged_speaker_turns_to_the_active_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker source = ProjectSpeaker.Create(db.Project.Id, "Speaker 1", db.Now.AddMilliseconds(1));
        ProjectSpeaker target = ProjectSpeaker.Create(db.Project.Id, "Speaker 2", db.Now.AddMilliseconds(2));
        SpeakerTurn[] turns = [SpeakerTurn.Create(db.Project.Id, source.Id, 0d, 5.8d, 0.9d, hasOverlap: false)];
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [source, target], turns, TestContext.Current.CancellationToken);
        await db.Transcripts.ReassignAndMergeSpeakersAsync(db.Project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

        // A replay of the same diarization result must not attach turns to the archived speaker.
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [source], turns, TestContext.Current.CancellationToken);

        IReadOnlyList<SpeakerTurn> reloadedTurns = await db.Speakers.ListTurnsAsync(db.Project.Id, TestContext.Current.CancellationToken);
        IReadOnlyList<ProjectSpeaker> speakers = await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);
        SpeakerTurn turn = Assert.Single(reloadedTurns);
        Assert.Equal(target.Id, turn.SpeakerId);
        ProjectSpeaker activeSpeaker = Assert.Single(speakers);
        Assert.Equal(target.Id, activeSpeaker.Id);
    }

    [Fact]
    public async Task ListMergedSpeakersAsync_returns_archived_speakers_with_their_merge_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker source = ProjectSpeaker.Create(db.Project.Id, "Speaker 1", db.Now.AddMilliseconds(1));
        ProjectSpeaker target = ProjectSpeaker.Create(db.Project.Id, "Speaker 2", db.Now.AddMilliseconds(2));
        SpeakerTurn[] turns = [SpeakerTurn.Create(db.Project.Id, source.Id, 0d, 5.8d, 0.9d, hasOverlap: false)];
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [source, target], turns, TestContext.Current.CancellationToken);
        await db.Transcripts.ReassignAndMergeSpeakersAsync(db.Project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

        IReadOnlyList<MergedSpeakerEntry> merged = await db.Speakers.ListMergedSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);
        IReadOnlyList<ProjectSpeaker> active = await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);

        MergedSpeakerEntry entry = Assert.Single(merged);
        Assert.Equal(source.Id, entry.Speaker.Id);
        Assert.Equal("Speaker 1", entry.Speaker.DisplayName);
        Assert.Equal(target.Id, entry.MergedIntoSpeakerId);
        Assert.DoesNotContain(active, speaker => speaker.Id == source.Id);
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_removes_a_speaker_with_no_references()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker speaker = await db.Speakers.CreateSpeakerAsync(db.Project.Id, TestContext.Current.CancellationToken);

        await db.Speakers.DeleteEmptySpeakerAsync(db.Project.Id, speaker.Id, TestContext.Current.CancellationToken);

        Assert.Empty(await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_speaker_with_turns()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker speaker = ProjectSpeaker.Create(db.Project.Id, "Speaker 1", db.Now);
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [speaker],
            [SpeakerTurn.Create(db.Project.Id, speaker.Id, 0d, 5.8d)],
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Speakers.DeleteEmptySpeakerAsync(db.Project.Id, speaker.Id, TestContext.Current.CancellationToken));
        Assert.Single(await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_speaker_with_a_voice_assignment()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker speaker = await db.Speakers.CreateSpeakerAsync(db.Project.Id, TestContext.Current.CancellationToken);
        await db.VoiceAssignments.SaveAsync(
            VoiceAssignment.Create(db.Project.Id, speaker.Id, "kokoro-onnx"),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Speakers.DeleteEmptySpeakerAsync(db.Project.Id, speaker.Id, TestContext.Current.CancellationToken));
        Assert.Single(await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_merged_speaker()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        (ProjectSpeaker source, ProjectSpeaker target) = await db.MergeTwoSpeakersAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Speakers.DeleteEmptySpeakerAsync(db.Project.Id, source.Id, TestContext.Current.CancellationToken));
        Assert.Single(await db.Speakers.ListMergedSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_speaker_that_is_a_merge_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        (ProjectSpeaker _, ProjectSpeaker target) = await db.MergeTwoSpeakersAsync();

        // The target has no turns, segments, or voice assignments of its own, but deleting it
        // would leave the archived source pointing at a missing speaker.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Speakers.DeleteEmptySpeakerAsync(db.Project.Id, target.Id, TestContext.Current.CancellationToken));
        Assert.Contains(
            await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken),
            speaker => speaker.Id == target.Id);
    }

    [Fact]
    public async Task ListMergedSpeakersAsync_resolves_merge_chains_to_the_surviving_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker first = ProjectSpeaker.Create(db.Project.Id, "Speaker 1", db.Now.AddMilliseconds(1));
        ProjectSpeaker second = ProjectSpeaker.Create(db.Project.Id, "Speaker 2", db.Now.AddMilliseconds(2));
        ProjectSpeaker third = ProjectSpeaker.Create(db.Project.Id, "Speaker 3", db.Now.AddMilliseconds(3));
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [first, second, third], [], TestContext.Current.CancellationToken);
        await db.Transcripts.ReassignAndMergeSpeakersAsync(db.Project.Id, first.Id, second.Id, TestContext.Current.CancellationToken);
        await db.Transcripts.ReassignAndMergeSpeakersAsync(db.Project.Id, second.Id, third.Id, TestContext.Current.CancellationToken);

        IReadOnlyList<MergedSpeakerEntry> merged = await db.Speakers.ListMergedSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, entry => Assert.Equal(third.Id, entry.MergedIntoSpeakerId));
    }

    [Fact]
    public async Task GetForSpeakerIncludingMergedAsync_returns_the_assignment_of_an_archived_speaker()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker source = ProjectSpeaker.Create(db.Project.Id, "Speaker 1", db.Now.AddMilliseconds(1));
        ProjectSpeaker target = ProjectSpeaker.Create(db.Project.Id, "Speaker 2", db.Now.AddMilliseconds(2));
        await db.Speakers.ReplaceDiarizationAsync(db.Project.Id, [source, target], [], TestContext.Current.CancellationToken);
        VoiceAssignment assignment = VoiceAssignment.Create(db.Project.Id, source.Id, "kokoro-onnx", "af-heart");
        await db.VoiceAssignments.SaveAsync(assignment, TestContext.Current.CancellationToken);
        await db.Transcripts.ReassignAndMergeSpeakersAsync(db.Project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);

        Assert.Null(await db.VoiceAssignments.GetAsync(db.Project.Id, source.Id, TestContext.Current.CancellationToken));
        VoiceAssignment? history = await db.VoiceAssignments.GetForSpeakerIncludingMergedAsync(db.Project.Id, source.Id, TestContext.Current.CancellationToken);

        Assert.NotNull(history);
        Assert.Equal(assignment.Id, history.Id);
        Assert.Equal(source.Id, history.SpeakerId);
        Assert.Equal("af-heart", history.VoiceVariant);
    }

    private sealed class SpeakerDb : IDisposable
    {
        private readonly string projectRoot;

        private SpeakerDb(string projectRoot)
        {
            this.projectRoot = projectRoot;
            Database = new SqliteProjectDatabase(projectRoot);
            Projects = new SqliteProjectRepository(Database);
            Speakers = new SqliteSpeakerRepository(Database);
            Transcripts = new SqliteTranscriptRepository(Database);
            VoiceAssignments = new SqliteVoiceAssignmentRepository(Database);
        }

        public SqliteProjectDatabase Database { get; }
        public SqliteProjectRepository Projects { get; }
        public SqliteSpeakerRepository Speakers { get; }
        public SqliteTranscriptRepository Transcripts { get; }
        public SqliteVoiceAssignmentRepository VoiceAssignments { get; }
        public TrackdubProject Project { get; private set; } = null!;
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

        public static async Task<SpeakerDb> CreateAsync(CancellationToken cancellationToken)
        {
            string projectRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Speakers.trackdub");
            var db = new SpeakerDb(projectRoot);
            DateTimeOffset now = db.Now;
            db.Project = new TrackdubProject(Guid.NewGuid(), "Speakers", now, now);
            await db.Projects.InitializeAsync(db.Project, cancellationToken);
            return db;
        }

        public async Task<(ProjectSpeaker Source, ProjectSpeaker Target)> MergeTwoSpeakersAsync()
        {
            ProjectSpeaker source = ProjectSpeaker.Create(Project.Id, "Speaker 1", Now.AddMilliseconds(1));
            ProjectSpeaker target = ProjectSpeaker.Create(Project.Id, "Speaker 2", Now.AddMilliseconds(2));
            await Speakers.ReplaceDiarizationAsync(Project.Id, [source, target], [], TestContext.Current.CancellationToken);
            await Transcripts.ReassignAndMergeSpeakersAsync(Project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);
            return (source, target);
        }

        public void Dispose()
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
