using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
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
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;
using System.Security.Cryptography;

#pragma warning disable CS0618

namespace Trackdub.Application.Tests;

public partial class TranscriptProjectServiceTests
{
    [Fact]
    public async Task RenameSpeakerAsync_updates_display_name_without_changing_id()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        ProjectSpeaker speaker = created.Speakers[0];
        TranscriptProjectState renamed = await scope.Service.RenameSpeakerAsync(
            new RenameSpeakerRequest(speaker.Id, "Host"),
            TestContext.Current.CancellationToken);

        ProjectSpeaker renamedSpeaker = Assert.Single(renamed.Speakers, candidate => candidate.Id == speaker.Id);
        Assert.Equal("Host", renamedSpeaker.DisplayName);
    }

    [Fact]
    public async Task MergeSpeakersAsync_reassigns_turns_and_deletes_source_speaker()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        ProjectSpeaker target = created.Speakers[0];
        ProjectSpeaker source = created.Speakers[1];
        TranscriptProjectState merged = await scope.Service.MergeSpeakersAsync(
            new MergeSpeakersRequest(source.Id, target.Id),
            TestContext.Current.CancellationToken);

        Assert.Single(merged.Speakers);
        Assert.DoesNotContain(merged.Speakers, speaker => speaker.Id == source.Id);
        Assert.All(merged.SpeakerTurns, turn => Assert.Equal(target.Id, turn.SpeakerId));
        Assert.DoesNotContain(scope.SpeakerRepository.Speakers, speaker => speaker.Id == source.Id);
    }

    [Fact]
    public async Task AssignSpeakerToSegmentAsync_creates_new_revision_with_override()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Guid targetSpeakerId = created.Speakers[1].Id;
        TranscriptProjectState reassigned = await scope.Service.AssignSpeakerToSegmentAsync(
            new AssignSpeakerToSegmentRequest(
                created.CurrentTranscriptRevision!.Id,
                created.TranscriptSegments[0].Id,
                targetSpeakerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, reassigned.CurrentTranscriptRevision!.RevisionNumber);
        Assert.Equal(targetSpeakerId, reassigned.TranscriptSegments[0].SpeakerId);
        Assert.Equal(Trackdub.Domain.Transcript.SpeakerAssignmentOrigin.Manual,
            reassigned.TranscriptSegments[0].SpeakerAssignmentOrigin);
    }

    [Fact]
    public async Task AssignSpeakerToSegmentsAsync_creates_single_revision_for_multiple_overrides()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Guid targetSpeakerId = created.Speakers[1].Id;
        TranscriptProjectState reassigned = await scope.Service.AssignSpeakerToSegmentsAsync(
            new AssignSpeakerToSegmentsRequest(
                created.CurrentTranscriptRevision!.Id,
                created.TranscriptSegments.Select(segment => segment.Id).ToArray(),
                targetSpeakerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, reassigned.CurrentTranscriptRevision!.RevisionNumber);
        Assert.All(reassigned.TranscriptSegments, segment => Assert.Equal(targetSpeakerId, segment.SpeakerId));
    }

    [Fact]
    public async Task CreateSpeakerFromSegmentsAsync_does_not_mark_translation_as_stale()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        Guid originalSegmentId = translated.TranscriptSegments[0].Id;
        Guid? originalSpeakerId = translated.TranscriptSegments[0].SpeakerId;

        TranscriptProjectState reassigned = await scope.Service.CreateSpeakerFromSegmentsAsync(
            new CreateSpeakerFromSegmentsRequest(translated.CurrentTranscriptRevision!.Id, [originalSegmentId]),
            TestContext.Current.CancellationToken);

        Assert.Equal(translated.CurrentTranslationRevision!.Id, reassigned.CurrentTranslationRevision!.Id);
        Assert.NotEqual(originalSpeakerId, reassigned.TranscriptSegments[0].SpeakerId);
        Assert.False(reassigned.IsTranslationStale);
        Assert.Empty(reassigned.StaleTranslatedSegmentIndices);
    }

    [Fact]
    public async Task SplitSpeakerTurnAsync_creates_two_turns()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        SpeakerTurn turn = created.SpeakerTurns[0];
        TranscriptProjectState split = await scope.Service.SplitSpeakerTurnAsync(
            new SplitSpeakerTurnRequest(turn.Id, 2.9),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, split.SpeakerTurns.Count);
        Assert.Contains(split.SpeakerTurns, candidate => Math.Abs(candidate.EndSeconds - 2.9) < 0.001);
        Assert.Contains(split.SpeakerTurns, candidate => Math.Abs(candidate.StartSeconds - 2.9) < 0.001);
    }

    [Fact]
    public async Task ExtractReferenceClipAsync_writes_artifact_and_registers_reference_clip()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        TranscriptProjectState clipped = await scope.Service.ExtractReferenceClipAsync(
            new ExtractReferenceClipRequest(created.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        ProjectArtifact artifact = Assert.Single(clipped.ProjectState.Artifacts, candidate => candidate.Kind == ArtifactKind.ReferenceClip);
        Assert.True(scope.ArtifactStore.Exists(artifact.RelativePath));
        Assert.Equal(ArtifactKind.ReferenceClip, artifact.Kind);
        Assert.Equal(1, scope.ReferenceClipTrimmer.TrimCallCount);
    }

    [Fact]
    public async Task ExtractReferenceClipAsync_prefers_current_vocal_route_over_normalized_mix()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var clipExtractor = new FakeAudioClipExtractor();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            audioClipExtractor: clipExtractor);
        TranscriptProjectState created = await CreateWithStemsAsync(
            scope,
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false));
        ProjectArtifact vocals = Assert.Single(created.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);

        await scope.Service.ExtractReferenceClipAsync(
            new ExtractReferenceClipRequest(created.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        Assert.Equal(scope.ArtifactStore.GetPath(vocals.RelativePath), clipExtractor.LastSourceWavePath);
    }

    [Fact]
    public async Task ImportReferenceClipAsync_deletes_committed_file_when_post_commit_metadata_fails()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        string referencePath = Path.Join(tempDirectory, "reference.wav");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(referencePath, [5, 6, 7, 8], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(
            tempDirectory,
            fileFingerprintService: new ThrowingReferenceClipFingerprintService());
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Workspace.Speakers.ImportReferenceClipAsync(
                new ImportReferenceClipRequest(created.Speakers[0].Id, referencePath),
                TestContext.Current.CancellationToken));

        string referenceClipRoot = scope.ArtifactStore.GetPath(ProjectArtifactPaths.ReferenceClipDirectoryRelativePath);
        bool hasReferenceClipFiles = Directory.Exists(referenceClipRoot) &&
                                     Directory.EnumerateFiles(referenceClipRoot, "*", SearchOption.AllDirectories).Any();
        Assert.False(hasReferenceClipFiles);
    }

    [Fact]
    public async Task ImportReferenceClipAsync_removes_saved_artifact_metadata_when_voice_assignment_fails()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        string referencePath = Path.Join(tempDirectory, "reference.wav");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(referencePath, [5, 6, 7, 8], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        scope.VoiceAssignmentRepository.ThrowOnSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Workspace.Speakers.ImportReferenceClipAsync(
                new ImportReferenceClipRequest(created.Speakers[0].Id, referencePath),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(scope.MediaAssetRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.ReferenceClip);
        string referenceClipRoot = scope.ArtifactStore.GetPath(ProjectArtifactPaths.ReferenceClipDirectoryRelativePath);
        bool hasReferenceClipFiles = Directory.Exists(referenceClipRoot) &&
                                     Directory.EnumerateFiles(referenceClipRoot, "*", SearchOption.AllDirectories).Any();
        Assert.False(hasReferenceClipFiles);
    }

    [Fact]
    public async Task ImportReferenceClipAsync_preserves_original_error_when_cleanup_delete_fails()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        string referencePath = Path.Join(tempDirectory, "reference.wav");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(referencePath, [5, 6, 7, 8], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        scope.VoiceAssignmentRepository.ThrowOnSave = true;
        scope.MediaAssetRepository.ThrowOnDeleteArtifact = true;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Workspace.Speakers.ImportReferenceClipAsync(
                new ImportReferenceClipRequest(created.Speakers[0].Id, referencePath),
                TestContext.Current.CancellationToken));

        Assert.Equal("Voice assignment save failed.", exception.Message);
    }
}
