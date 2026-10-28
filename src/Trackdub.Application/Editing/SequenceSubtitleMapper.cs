using Trackdub.Application.Transcripts;
using Trackdub.Domain.Editing;

namespace Trackdub.Application.Editing;

/// <summary>Rebases source cues for a complete sequence or one selected placement.</summary>
public static class SequenceSubtitleMapper
{
    public static IReadOnlyList<SubtitleCue> Map(
        SequenceDocument document,
        IReadOnlyDictionary<Guid, IReadOnlyList<SubtitleCue>> cuesBySource,
        Guid? selectedPlacementId = null)
    {
        ArgumentNullException.ThrowIfNull(cuesBySource);
        IReadOnlyList<SequencePlacementPosition> positions = SequenceEditor.GetPositions(document);
        if (selectedPlacementId is Guid id)
        {
            SequencePlacementPosition selected = positions.FirstOrDefault(position => position.Placement.Id == id)
                ?? throw new InvalidOperationException("The selected placement was not found.");
            positions = [selected with { SequenceStartSeconds = 0, SequenceEndSeconds = selected.SequenceEndSeconds - selected.SequenceStartSeconds }];
        }

        var result = new List<SubtitleCue>();
        foreach (SequencePlacementPosition position in positions)
        {
            if (!cuesBySource.TryGetValue(position.Placement.SourceId, out IReadOnlyList<SubtitleCue>? cues) || cues is null)
            {
                throw new InvalidOperationException("Subtitle data is unavailable for a sequence source.");
            }
            foreach (SubtitleCue cue in cues.OrderBy(static item => item.StartSeconds).ThenBy(static item => item.SegmentIndex))
            {
                if (string.IsNullOrWhiteSpace(cue.Text))
                {
                    continue;
                }
                SequenceRange? range = SequenceEditor.RebaseRange(position, cue.StartSeconds, cue.EndSeconds);
                if (range is not null)
                {
                    result.Add(cue with { SegmentIndex = result.Count, StartSeconds = range.StartSeconds, EndSeconds = range.EndSeconds });
                }
            }
        }
        return result;
    }
}
