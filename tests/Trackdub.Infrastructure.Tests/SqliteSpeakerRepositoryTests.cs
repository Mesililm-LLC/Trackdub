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

        ProjectSpeaker speaker2 = db.NewSpeaker(2);
        ProjectSpeaker speaker3 = db.NewSpeaker(3);
        await db.ReplaceAsync([speaker2, speaker3], [db.NewTurn(speaker2, 0d), db.NewTurn(speaker3, 6d)]);

        ReloadedState initial = await db.ReloadAsync(revision.Id);
        AssertInitialReload(initial, defaultSpeaker, speaker2, speaker3);

        ProjectSpeaker speaker4 = db.NewSpeaker(4);
        await db.ReplaceAsync([speaker4], [db.NewTurn(speaker4, 0d)]);
        ReloadedState rerun = await db.ReloadAsync(revision.Id);
        AssertRerunKeptDefaultSpeaker(rerun, defaultSpeaker, speaker4);
    }

    private static void AssertInitialReload(
        ReloadedState initial,
        ProjectSpeaker defaultSpeaker,
        ProjectSpeaker speaker2,
        ProjectSpeaker speaker3)
    {
        Assert.Equal(3, initial.Speakers.Count);
        Assert.Contains(initial.Speakers, speaker => speaker.Id == defaultSpeaker.Id);
        Assert.Contains(initial.Speakers, speaker => speaker.Id == speaker2.Id);
        Assert.Contains(initial.Speakers, speaker => speaker.Id == speaker3.Id);
        Assert.Equal(2, initial.Turns.Count);
        Assert.Equal(defaultSpeaker.Id, Assert.Single(initial.Segments).SpeakerId);
    }

    private static void AssertRerunKeptDefaultSpeaker(
        ReloadedState reloaded,
        ProjectSpeaker defaultSpeaker,
        ProjectSpeaker rerunSpeaker)
    {
        Assert.Contains(reloaded.Speakers, speaker => speaker.Id == defaultSpeaker.Id);
        Assert.Contains(reloaded.Speakers, speaker => speaker.Id == rerunSpeaker.Id);
        Assert.Equal(rerunSpeaker.Id, Assert.Single(reloaded.Turns).SpeakerId);
        Assert.Equal(defaultSpeaker.Id, Assert.Single(reloaded.Segments).SpeakerId);
    }

    [Fact]
    public async Task ReplaceDiarizationAsync_keeps_existing_speakers_when_a_rerun_resupplies_their_ids()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker speaker = db.NewSpeaker(1);
        SpeakerTurn[] turns = [db.NewTurn(speaker, 0d)];

        await db.ReplaceAsync([speaker], turns);

        // A cached rerun or fixture can resupply the same speaker identity.
        await db.ReplaceAsync([speaker], turns);

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
        (ProjectSpeaker source, ProjectSpeaker target, SpeakerTurn turn) = await db.MergeTwoSpeakersWithTurnAsync();

        // A replay of the same diarization result must not attach turns to the archived speaker.
        await db.ReplaceAsync([source], [turn]);

        IReadOnlyList<SpeakerTurn> reloadedTurns = await db.Speakers.ListTurnsAsync(db.Project.Id, TestContext.Current.CancellationToken);
        IReadOnlyList<ProjectSpeaker> speakers = await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, Assert.Single(reloadedTurns).SpeakerId);
        Assert.Equal(target.Id, Assert.Single(speakers).Id);
    }

    [Fact]
    public async Task ListMergedSpeakersAsync_returns_archived_speakers_with_their_merge_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        (ProjectSpeaker source, ProjectSpeaker target, SpeakerTurn _) = await db.MergeTwoSpeakersWithTurnAsync();

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
        ProjectSpeaker speaker = db.NewSpeaker(1);
        await db.ReplaceAsync([speaker], [db.NewTurn(speaker, 0d)]);

        await AssertDeleteRefused(db, speaker.Id);
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

        await AssertDeleteRefused(db, speaker.Id);
        Assert.Single(await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_merged_speaker()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        (ProjectSpeaker source, ProjectSpeaker _) = await db.MergeTwoSpeakersAsync();

        await AssertDeleteRefused(db, source.Id);
        Assert.Single(await db.Speakers.ListMergedSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_speaker_that_is_a_merge_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        (ProjectSpeaker _, ProjectSpeaker target) = await db.MergeTwoSpeakersAsync();

        // The target has no turns, segments, or voice assignments of its own, but deleting it
        // would leave the archived source pointing at a missing speaker.
        await AssertDeleteRefused(db, target.Id);
        Assert.Contains(
            await db.Speakers.ListSpeakersAsync(db.Project.Id, TestContext.Current.CancellationToken),
            speaker => speaker.Id == target.Id);
    }

    private static Task AssertDeleteRefused(SpeakerDb db, Guid speakerId) =>
        Assert.ThrowsAsync<InvalidOperationException>(() =>
            db.Speakers.DeleteEmptySpeakerAsync(db.Project.Id, speakerId, TestContext.Current.CancellationToken));

    [Fact]
    public async Task ListMergedSpeakersAsync_resolves_merge_chains_to_the_surviving_target()
    {
        using SpeakerDb db = await SpeakerDb.CreateAsync(TestContext.Current.CancellationToken);
        ProjectSpeaker first = db.NewSpeaker(1);
        ProjectSpeaker second = db.NewSpeaker(2);
        ProjectSpeaker third = db.NewSpeaker(3);
        await db.ReplaceAsync([first, second, third], []);
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
        ProjectSpeaker source = db.NewSpeaker(1);
        ProjectSpeaker target = db.NewSpeaker(2);
        await db.ReplaceAsync([source, target], []);
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

    private sealed record ReloadedState(
        IReadOnlyList<ProjectSpeaker> Speakers,
        IReadOnlyList<SpeakerTurn> Turns,
        IReadOnlyList<TranscriptSegment> Segments);

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

        public ProjectSpeaker NewSpeaker(int index) =>
            ProjectSpeaker.Create(Project.Id, $"Speaker {index}", Now.AddMilliseconds(index));

        public SpeakerTurn NewTurn(ProjectSpeaker speaker, double startSeconds) =>
            SpeakerTurn.Create(Project.Id, speaker.Id, startSeconds, startSeconds + 5.8d, 0.9d, hasOverlap: false);

        public Task ReplaceAsync(ProjectSpeaker[] speakers, SpeakerTurn[] turns) =>
            Speakers.ReplaceDiarizationAsync(Project.Id, speakers, turns, TestContext.Current.CancellationToken);

        public async Task<(ProjectSpeaker Source, ProjectSpeaker Target)> MergeTwoSpeakersAsync()
        {
            ProjectSpeaker source = NewSpeaker(1);
            ProjectSpeaker target = NewSpeaker(2);
            await ReplaceAsync([source, target], []);
            await Transcripts.ReassignAndMergeSpeakersAsync(Project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);
            return (source, target);
        }

        public async Task<(ProjectSpeaker Source, ProjectSpeaker Target, SpeakerTurn Turn)> MergeTwoSpeakersWithTurnAsync()
        {
            ProjectSpeaker source = NewSpeaker(1);
            ProjectSpeaker target = NewSpeaker(2);
            SpeakerTurn turn = NewTurn(source, 0d);
            await ReplaceAsync([source, target], [turn]);
            await Transcripts.ReassignAndMergeSpeakersAsync(Project.Id, source.Id, target.Id, TestContext.Current.CancellationToken);
            return (source, target, turn);
        }

        public async Task<ReloadedState> ReloadAsync(Guid revisionId)
        {
            IReadOnlyList<ProjectSpeaker> speakers = await Speakers.ListSpeakersAsync(Project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<SpeakerTurn> turns = await Speakers.ListTurnsAsync(Project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<TranscriptSegment> segments = await Transcripts.GetSegmentsAsync(revisionId, TestContext.Current.CancellationToken);
            return new ReloadedState(speakers, turns, segments);
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
