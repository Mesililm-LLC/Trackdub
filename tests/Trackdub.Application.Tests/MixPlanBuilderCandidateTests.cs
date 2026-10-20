using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Mixing;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class MixPlanBuilderCandidateTests
{
    [Fact]
    public void Build_uses_selected_candidate_take_when_candidate_group_is_present()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid transcriptRevisionId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();

        TranscriptSegment segment = TranscriptSegment.Create(
            transcriptRevisionId, 0, 0.0d, 2.0d, "Hello", speakerId);
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            translationRevisionId, 0, 0.0d, 2.0d, "Hola");

        ProjectArtifact normalized = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "norm-hash", 100, 10.0d, 48000, 1, DateTimeOffset.UtcNow);

        // Primary take (the regular stage-generated take)
        ProjectArtifact primaryArtifact = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.TtsTake,
            "artifacts/tts/primary.wav", "primary-hash", 100, 1.8d, 48000, 1,
            DateTimeOffset.UtcNow.AddSeconds(1));
        TtsTake primaryTake = TtsTake.Create(
                projectId, voiceAssignmentId, translatedSegmentId: translatedSegment.Id,
                segmentIndex: segment.SegmentIndex)
            .Complete(primaryArtifact.Id, 48000 * 2, 48000, "fake");

        // Candidate A — the selected one
        Guid candidateGroupId = Guid.NewGuid();
        ProjectArtifact candidateAArtifact = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.TtsTake,
            "artifacts/tts/candidate-a.wav", "cand-a-hash", 100, 1.5d, 48000, 1,
            DateTimeOffset.UtcNow.AddSeconds(2));
        TtsTake candidateATake = (TtsTake.Create(
                projectId, voiceAssignmentId, translatedSegmentId: translatedSegment.Id,
                segmentIndex: segment.SegmentIndex)
            .Complete(candidateAArtifact.Id, 48000, 48000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 0,
            Variant = TtsCandidateVariant.Candidate
        };

        // Candidate B
        ProjectArtifact candidateBArtifact = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.TtsTake,
            "artifacts/tts/candidate-b.wav", "cand-b-hash", 100, 1.6d, 48000, 1,
            DateTimeOffset.UtcNow.AddSeconds(3));
        TtsTake candidateBTake = (TtsTake.Create(
                projectId, voiceAssignmentId, translatedSegmentId: translatedSegment.Id,
                segmentIndex: segment.SegmentIndex)
            .Complete(candidateBArtifact.Id, 48000, 48000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 1,
            Variant = TtsCandidateVariant.Candidate
        };

        // Group: candidate A is selected
        TtsCandidateGroup group = TtsCandidateGroup.Create(
            projectId, translatedSegment.Id, 0, candidateATake.Id);

        MixPlan plan = new MixPlanBuilder().Build(new MixPlanBuildRequest(
            projectId,
            mediaAssetId,
            [normalized, primaryArtifact, candidateAArtifact, candidateBArtifact],
            [segment],
            [translatedSegment],
            [primaryTake, candidateATake, candidateBTake],
            CandidateGroups: [group], StageRuns: null));

        Assert.Single(plan.SpeechClips);
        Assert.Equal(candidateAArtifact.RelativePath, plan.SpeechClips[0].TakeRelativePath);
        Assert.False(plan.SpeechClips[0].IsSilentGap);
    }

    [Fact]
    public void Build_falls_back_to_primary_take_when_no_candidate_group_provided()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid transcriptRevisionId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();

        TranscriptSegment segment = TranscriptSegment.Create(
            transcriptRevisionId, 0, 0.0d, 2.0d, "Hello", speakerId);
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            translationRevisionId, 0, 0.0d, 2.0d, "Hola");

        ProjectArtifact normalized = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "norm-hash", 100, 10.0d, 48000, 1, DateTimeOffset.UtcNow);

        ProjectArtifact primaryArtifact = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.TtsTake,
            "artifacts/tts/primary.wav", "primary-hash", 100, 1.8d, 48000, 1,
            DateTimeOffset.UtcNow.AddSeconds(1));
        TtsTake primaryTake = TtsTake.Create(
                projectId, voiceAssignmentId, translatedSegmentId: translatedSegment.Id,
                segmentIndex: segment.SegmentIndex)
            .Complete(primaryArtifact.Id, 48000 * 2, 48000, "fake");

        // No candidate groups passed
        MixPlan plan = new MixPlanBuilder().Build(new MixPlanBuildRequest(
            projectId,
            mediaAssetId,
            [normalized, primaryArtifact],
            [segment],
            [translatedSegment],
            [primaryTake], StageRuns: null));

        Assert.Single(plan.SpeechClips);
        Assert.Equal(primaryArtifact.RelativePath, plan.SpeechClips[0].TakeRelativePath);
        Assert.False(plan.SpeechClips[0].IsSilentGap);
    }

    [Fact]
    public void Build_uses_selected_candidate_B_when_B_is_selected()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid transcriptRevisionId = Guid.NewGuid();
        Guid translationRevisionId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        Guid voiceAssignmentId = Guid.NewGuid();

        TranscriptSegment segment = TranscriptSegment.Create(
            transcriptRevisionId, 0, 0.0d, 2.0d, "Hello", speakerId);
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            translationRevisionId, 0, 0.0d, 2.0d, "Hola");

        ProjectArtifact normalized = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "norm-hash", 100, 10.0d, 48000, 1, DateTimeOffset.UtcNow);

        Guid candidateGroupId = Guid.NewGuid();

        ProjectArtifact candidateAArtifact = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.TtsTake,
            "artifacts/tts/candidate-a.wav", "cand-a-hash", 100, 1.5d, 48000, 1,
            DateTimeOffset.UtcNow.AddSeconds(1));
        TtsTake candidateATake = (TtsTake.Create(
                projectId, voiceAssignmentId, translatedSegmentId: translatedSegment.Id,
                segmentIndex: segment.SegmentIndex)
            .Complete(candidateAArtifact.Id, 48000, 48000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 0,
            Variant = TtsCandidateVariant.Candidate
        };

        ProjectArtifact candidateBArtifact = new(
            Guid.NewGuid(), projectId, mediaAssetId, ArtifactKind.TtsTake,
            "artifacts/tts/candidate-b.wav", "cand-b-hash", 100, 1.6d, 48000, 1,
            DateTimeOffset.UtcNow.AddSeconds(2));
        TtsTake candidateBTake = (TtsTake.Create(
                projectId, voiceAssignmentId, translatedSegmentId: translatedSegment.Id,
                segmentIndex: segment.SegmentIndex)
            .Complete(candidateBArtifact.Id, 48000, 48000, "fake")) with
        {
            CandidateGroupId = candidateGroupId,
            CandidateIndex = 1,
            Variant = TtsCandidateVariant.Candidate
        };

        // Group: candidate B is selected
        TtsCandidateGroup group = TtsCandidateGroup.Create(
            projectId, translatedSegment.Id, 0, candidateBTake.Id);

        MixPlan plan = new MixPlanBuilder().Build(new MixPlanBuildRequest(
            projectId,
            mediaAssetId,
            [normalized, candidateAArtifact, candidateBArtifact],
            [segment],
            [translatedSegment],
            [candidateATake, candidateBTake],
            CandidateGroups: [group], StageRuns: null));

        Assert.Single(plan.SpeechClips);
        Assert.Equal(candidateBArtifact.RelativePath, plan.SpeechClips[0].TakeRelativePath);
    }
}
