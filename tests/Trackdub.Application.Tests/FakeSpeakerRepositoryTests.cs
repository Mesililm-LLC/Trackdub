using Trackdub.Domain.Speakers;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// Verifies that the shared test doubles reproduce the real repositories' reference guards, so
/// application tests cannot succeed in a state the SQLite repositories would reject.
/// </summary>
public sealed class FakeSpeakerRepositoryTests
{
    private readonly Guid projectId = Guid.NewGuid();

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_segment_reference()
    {
        FakeSpeakerRepository repository = new();
        ProjectSpeaker speaker = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        repository.Seed([speaker]);
        repository.SeedReferences(segmentSpeakerIds: [speaker.Id]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteEmptySpeakerAsync(projectId, speaker.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_voice_assignment_reference()
    {
        FakeSpeakerRepository repository = new();
        ProjectSpeaker speaker = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        repository.Seed([speaker]);
        repository.SeedReferences(voiceAssignmentSpeakerIds: [speaker.Id]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteEmptySpeakerAsync(projectId, speaker.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_merged_source()
    {
        FakeSpeakerRepository repository = new();
        ProjectSpeaker source = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        ProjectSpeaker target = ProjectSpeaker.Create(projectId, "Speaker 2", DateTimeOffset.UtcNow.AddMilliseconds(1));
        repository.Seed([source, target]);
        repository.SeedReferences(mergedSpeakerTargets: new Dictionary<Guid, Guid> { [source.Id] = target.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteEmptySpeakerAsync(projectId, source.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_refuses_a_merge_target()
    {
        FakeSpeakerRepository repository = new();
        ProjectSpeaker source = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        ProjectSpeaker target = ProjectSpeaker.Create(projectId, "Speaker 2", DateTimeOffset.UtcNow.AddMilliseconds(1));
        repository.Seed([source, target]);
        repository.SeedReferences(mergedSpeakerTargets: new Dictionary<Guid, Guid> { [source.Id] = target.Id });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.DeleteEmptySpeakerAsync(projectId, target.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteEmptySpeakerAsync_removes_a_speaker_with_no_references()
    {
        FakeSpeakerRepository repository = new();
        ProjectSpeaker speaker = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        repository.Seed([speaker]);

        await repository.DeleteEmptySpeakerAsync(projectId, speaker.Id, TestContext.Current.CancellationToken);

        Assert.Empty(await repository.ListSpeakersAsync(projectId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SeedMergedSpeakers_hides_assignments_from_active_lookups_but_keeps_history()
    {
        FakeVoiceAssignmentRepository repository = new();
        Guid speakerId = Guid.NewGuid();
        VoiceAssignment assignment = VoiceAssignment.Create(projectId, speakerId, "kokoro-onnx");
        repository.Seed(assignment);
        repository.SeedMergedSpeakers([speakerId]);

        Assert.Null(await repository.GetAsync(projectId, speakerId, TestContext.Current.CancellationToken));
        Assert.Empty(await repository.GetAllAsync(projectId, TestContext.Current.CancellationToken));
        Assert.Equal(
            assignment.Id,
            (await repository.GetForSpeakerIncludingMergedAsync(projectId, speakerId, TestContext.Current.CancellationToken))?.Id);
    }
}
