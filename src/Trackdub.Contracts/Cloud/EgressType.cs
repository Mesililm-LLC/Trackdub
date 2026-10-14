namespace Trackdub.Contracts.Cloud;

/// <summary>
/// Categorizes the sensitivity and format of data leaving the local machine for a cloud provider.
/// </summary>
public enum EgressType
{
    /// <summary>Raw speech audio bytes sent to a cloud provider (ASR stage).</summary>
    Audio = 0,

    /// <summary>Transcript or translated text segments sent to a cloud provider (Translation, Text Refinement, TTS stages).</summary>
    Text = 1,

    /// <summary>Entire source media file (video and audio bytes) sent to a cloud provider (cloud dubbing lane).</summary>
    Media = 2,
}
