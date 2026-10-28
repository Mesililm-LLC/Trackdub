namespace Trackdub.Domain.Editing;

/// <summary>A saved edit sequence. Placements are ordered and share one output frame.</summary>
public sealed record SequenceDocument(
    int Version,
    Guid ProjectId,
    SequenceFrame? Frame,
    IReadOnlyList<SequenceSource> Sources,
    IReadOnlyList<SequencePlacement> Placements)
{
    public const int CurrentVersion = 1;

    public static SequenceDocument Create(Guid projectId) =>
        projectId == Guid.Empty
            ? throw new ArgumentException("Project id is required.", nameof(projectId))
            : new(CurrentVersion, projectId, null, [], []);
}

/// <param name="WorkspaceRelativePath">
/// Null for the original project; otherwise the project-relative path of the source's child workspace.
/// Source media paths and transcript artifacts remain owned by that workspace.
/// </param>
public sealed record SequenceSource(
    Guid Id,
    Guid ProjectId,
    Guid MediaAssetId,
    string? WorkspaceRelativePath,
    double DurationSeconds,
    int Width,
    int Height);

public sealed record SequenceFrame(int Width, int Height);

/// <summary>Focus coordinates are normalized from 0 to 1; zoom 1 is the uncropped baseline.</summary>
public sealed record SequenceCrop(double Zoom = 1, double FocusX = 0.5, double FocusY = 0.5);

public sealed record SequencePlacement(
    Guid Id,
    Guid SourceId,
    double SourceInSeconds,
    double SourceOutSeconds,
    SequenceCrop Crop);

public sealed record SequencePlacementPosition(
    SequencePlacement Placement,
    double SequenceStartSeconds,
    double SequenceEndSeconds);

public sealed record SequenceRange(double StartSeconds, double EndSeconds);
