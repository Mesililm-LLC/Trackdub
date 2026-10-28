namespace Trackdub.Domain.Speakers;

/// <summary>
/// An archived speaker retained as merge history. Merged speakers are never deleted;
/// this entry links the archived identity to the surviving merge target.
/// </summary>
public sealed record MergedSpeakerEntry(ProjectSpeaker Speaker, Guid MergedIntoSpeakerId);
