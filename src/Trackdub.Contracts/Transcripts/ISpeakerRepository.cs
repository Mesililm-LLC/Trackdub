using Trackdub.Domain.Speakers;

namespace Trackdub.Contracts.Transcripts;

public interface ISpeakerRepository
{
    Task<IReadOnlyList<ProjectSpeaker>> ListSpeakersAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Lists archived speakers retained as merge history, each with its surviving merge target.
    /// </summary>
    Task<IReadOnlyList<MergedSpeakerEntry>> ListMergedSpeakersAsync(Guid projectId, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes an active speaker only when nothing references it: no turns, no transcript
    /// segments in any revision, no voice assignments and no takes. Merged (archived)
    /// speakers are history and are never deleted.
    /// </summary>
    Task DeleteEmptySpeakerAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SpeakerTurn>> ListTurnsAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectSpeaker> EnsureDefaultSpeakerAsync(Guid projectId, CancellationToken cancellationToken);

    Task<ProjectSpeaker> CreateSpeakerAsync(Guid projectId, CancellationToken cancellationToken);

    Task ReplaceDiarizationAsync(
        Guid projectId,
        IReadOnlyList<ProjectSpeaker> speakers,
        IReadOnlyList<SpeakerTurn> turns,
        CancellationToken cancellationToken);

    Task RenameSpeakerAsync(
        Guid projectId,
        Guid speakerId,
        string displayName,
        CancellationToken cancellationToken);

    Task SplitTurnAsync(
        Guid projectId,
        Guid speakerTurnId,
        double splitSeconds,
        CancellationToken cancellationToken);
}
