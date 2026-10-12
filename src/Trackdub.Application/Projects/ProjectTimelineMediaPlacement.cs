namespace Trackdub.Application.Projects;

/// <summary>
/// Durable media-bin clip placed on the timeline UI (track index and timing in seconds).
/// </summary>
public sealed record ProjectTimelineMediaPlacement(
    string MediaPath,
    string DisplayName,
    int TrackIndex,
    double StartSeconds,
    double DurationSeconds,
    bool IsPrimarySource = false)
{
    public ProjectTimelineMediaPlacement Normalize() =>
        this with
        {
            MediaPath = MediaPath.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName)
                ? Path.GetFileName(MediaPath)
                : DisplayName.Trim(),
            TrackIndex = Math.Max(0, TrackIndex),
            StartSeconds = Math.Max(0d, StartSeconds),
            DurationSeconds = Math.Max(0.1, DurationSeconds),
        };
}
