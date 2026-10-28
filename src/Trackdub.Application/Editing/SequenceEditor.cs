using Trackdub.Domain.Editing;

namespace Trackdub.Application.Editing;

/// <summary>Pure editing operations. Video, audio and subtitles share each placement's source range.</summary>
public static class SequenceEditor
{
    public static SequenceDocument AddSource(SequenceDocument document, SequenceSource source)
    {
        Validate(document);
        ArgumentNullException.ThrowIfNull(source);
        return Checked(document with { Sources = [.. document.Sources, source] });
    }

    public static SequenceDocument AddPlacement(SequenceDocument document, SequencePlacement placement)
    {
        Validate(document);
        ArgumentNullException.ThrowIfNull(placement);
        SequenceSource source = GetSource(document, placement.SourceId);
        return Checked(document with
        {
            Frame = document.Frame ?? new SequenceFrame(source.Width, source.Height),
            Placements = [.. document.Placements, placement],
        });
    }

    public static SequenceDocument Trim(SequenceDocument document, Guid placementId, double sourceIn, double sourceOut) =>
        Replace(document, placementId, placement => placement with { SourceInSeconds = sourceIn, SourceOutSeconds = sourceOut });

    public static SequenceDocument SetCrop(SequenceDocument document, Guid placementId, SequenceCrop crop)
    {
        ArgumentNullException.ThrowIfNull(crop);
        return Replace(document, placementId, placement => placement with { Crop = crop });
    }

    public static SequenceDocument SetFrame(SequenceDocument document, SequenceFrame frame)
    {
        Validate(document);
        ArgumentNullException.ThrowIfNull(frame);
        return Checked(document with { Frame = frame });
    }

    public static SequenceDocument Split(SequenceDocument document, Guid placementId, double sequenceTime, Guid newPlacementId)
    {
        Validate(document);
        SequencePlacementPosition position = GetPositions(document).SingleOrDefault(item => item.Placement.Id == placementId)
            ?? throw new InvalidOperationException("The placement was not found.");
        if (!double.IsFinite(sequenceTime) || sequenceTime <= position.SequenceStartSeconds || sequenceTime >= position.SequenceEndSeconds)
        {
            throw new InvalidOperationException("The split must be inside the selected placement.");
        }

        double sourceTime = position.Placement.SourceInSeconds + sequenceTime - position.SequenceStartSeconds;
        var placements = new List<SequencePlacement>(document.Placements.Count + 1);
        foreach (SequencePlacement placement in document.Placements)
        {
            if (placement.Id == placementId)
            {
                placements.Add(placement with { SourceOutSeconds = sourceTime });
                placements.Add(placement with { Id = newPlacementId, SourceInSeconds = sourceTime });
            }
            else
            {
                placements.Add(placement);
            }
        }

        return Checked(document with { Placements = placements.ToArray() });
    }

    /// <param name="index">Destination index in the list after the placement is removed.</param>
    public static SequenceDocument Move(SequenceDocument document, Guid placementId, int index)
    {
        Validate(document);
        List<SequencePlacement> placements = [.. document.Placements];
        int oldIndex = placements.FindIndex(item => item.Id == placementId);
        if (oldIndex < 0)
        {
            throw new InvalidOperationException("The placement was not found.");
        }
        SequencePlacement placement = placements[oldIndex];
        placements.RemoveAt(oldIndex);
        if (index < 0 || index > placements.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        placements.Insert(index, placement);
        return Checked(document with { Placements = placements.ToArray() });
    }

    public static SequenceDocument Remove(SequenceDocument document, Guid placementId)
    {
        Validate(document);
        if (!document.Placements.Any(placement => placement.Id == placementId))
        {
            throw new InvalidOperationException("The placement was not found.");
        }
        return Checked(document with { Placements = document.Placements.Where(placement => placement.Id != placementId).ToArray() });
    }

    public static IReadOnlyList<SequencePlacementPosition> GetPositions(SequenceDocument document)
    {
        Validate(document);
        var result = new List<SequencePlacementPosition>(document.Placements.Count);
        double offset = 0;
        foreach (SequencePlacement placement in document.Placements)
        {
            double end = offset + placement.SourceOutSeconds - placement.SourceInSeconds;
            if (!double.IsFinite(end))
            {
                throw new InvalidDataException("The sequence duration is invalid.");
            }
            result.Add(new SequencePlacementPosition(placement, offset, end));
            offset = end;
        }
        return result;
    }

    /// <summary>Intersect a source-timed range with a placement and rebase it to the sequence.</summary>
    public static SequenceRange? RebaseRange(SequencePlacementPosition position, double sourceStart, double sourceEnd)
    {
        ArgumentNullException.ThrowIfNull(position);
        if (!double.IsFinite(sourceStart) || !double.IsFinite(sourceEnd) || sourceStart < 0 || sourceEnd <= sourceStart)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceStart), "The source range must be finite and nonempty.");
        }
        double start = Math.Max(sourceStart, position.Placement.SourceInSeconds);
        double end = Math.Min(sourceEnd, position.Placement.SourceOutSeconds);
        return end <= start ? null : new SequenceRange(
            position.SequenceStartSeconds + start - position.Placement.SourceInSeconds,
            position.SequenceStartSeconds + end - position.Placement.SourceInSeconds);
    }

    public static void Validate(SequenceDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Version != SequenceDocument.CurrentVersion)
        {
            throw new InvalidDataException($"Unsupported sequence version {document.Version}.");
        }
        if (document.ProjectId == Guid.Empty || document.Sources is null || document.Placements is null)
        {
            throw new InvalidDataException("The sequence has missing project or collection data.");
        }
        if (document.Frame is { Width: <= 0 } or { Height: <= 0 } ||
            (document.Placements.Count > 0 && document.Frame is null))
        {
            throw new InvalidDataException("The sequence output frame is invalid.");
        }

        var sources = new Dictionary<Guid, SequenceSource>();
        var projectIds = new HashSet<Guid>();
        var workspacePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SequenceSource? source in document.Sources)
        {
            if (source is null || source.Id == Guid.Empty || source.ProjectId == Guid.Empty || source.MediaAssetId == Guid.Empty ||
                !double.IsFinite(source.DurationSeconds) || source.DurationSeconds <= 0 || source.Width <= 0 || source.Height <= 0 ||
                !sources.TryAdd(source.Id, source) || !projectIds.Add(source.ProjectId))
            {
                throw new InvalidDataException("The sequence contains an invalid or duplicate source.");
            }
            if (source.WorkspaceRelativePath is null)
            {
                if (source.ProjectId != document.ProjectId)
                {
                    throw new InvalidDataException("Only the original project can use the root workspace.");
                }
            }
            else if (source.ProjectId == document.ProjectId || !IsSafeRelativePath(source.WorkspaceRelativePath) ||
                     !workspacePaths.Add(source.WorkspaceRelativePath.Replace('\\', '/')))
            {
                throw new InvalidDataException("A child source workspace path is invalid or duplicated.");
            }
        }

        var placementIds = new HashSet<Guid>();
        double duration = 0;
        foreach (SequencePlacement? placement in document.Placements)
        {
            if (placement is null || placement.Id == Guid.Empty || !placementIds.Add(placement.Id) ||
                !sources.TryGetValue(placement.SourceId, out SequenceSource? source) ||
                !double.IsFinite(placement.SourceInSeconds) || !double.IsFinite(placement.SourceOutSeconds) ||
                placement.SourceInSeconds < 0 || placement.SourceOutSeconds <= placement.SourceInSeconds ||
                placement.SourceOutSeconds > source.DurationSeconds || placement.Crop is null ||
                !double.IsFinite(placement.Crop.Zoom) || placement.Crop.Zoom < 1 ||
                !double.IsFinite(placement.Crop.FocusX) || placement.Crop.FocusX is < 0 or > 1 ||
                !double.IsFinite(placement.Crop.FocusY) || placement.Crop.FocusY is < 0 or > 1)
            {
                throw new InvalidDataException("The sequence contains an invalid placement, source range or crop.");
            }
            duration += placement.SourceOutSeconds - placement.SourceInSeconds;
            if (!double.IsFinite(duration))
            {
                throw new InvalidDataException("The sequence duration is invalid.");
            }
        }
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains(':') || path.StartsWith('/') || path.StartsWith('\\'))
        {
            return false;
        }
        return path.Split(['/', '\\']).All(static part => part.Length > 0 && part is not ("." or ".."));
    }

    private static SequenceDocument Replace(SequenceDocument document, Guid placementId, Func<SequencePlacement, SequencePlacement> replace)
    {
        Validate(document);
        if (!document.Placements.Any(placement => placement.Id == placementId))
        {
            throw new InvalidOperationException("The placement was not found.");
        }
        return Checked(document with
        {
            Placements = document.Placements.Select(placement => placement.Id == placementId ? replace(placement) : placement).ToArray(),
        });
    }

    private static SequenceSource GetSource(SequenceDocument document, Guid sourceId) =>
        document.Sources.FirstOrDefault(source => source.Id == sourceId)
        ?? throw new InvalidOperationException("The source was not found.");

    private static SequenceDocument Checked(SequenceDocument document)
    {
        Validate(document);
        return document;
    }
}
