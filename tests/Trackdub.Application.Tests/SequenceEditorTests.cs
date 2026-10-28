using Trackdub.Application.Editing;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.Editing;

namespace Trackdub.Application.Tests;

public sealed class SequenceEditorTests
{
    [Fact]
    public void Subtitles_rebase_duplicate_placements_and_selected_placement_to_zero()
    {
        Guid projectId = Guid.NewGuid();
        var source = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 60, 1920, 1080);
        Guid selectedId = Guid.NewGuid();
        SequenceDocument document = SequenceEditor.AddSource(SequenceDocument.Create(projectId), source);
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(Guid.NewGuid(), source.Id, 10, 20, new SequenceCrop()));
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(selectedId, source.Id, 10, 20, new SequenceCrop()));
        IReadOnlyDictionary<Guid, IReadOnlyList<SubtitleCue>> cues = new Dictionary<Guid, IReadOnlyList<SubtitleCue>>
        {
            [source.Id] = [new SubtitleCue(Guid.NewGuid(), 0, 8, 12, "First"), new SubtitleCue(Guid.NewGuid(), 1, 18, 22, "Last")],
        };

        IReadOnlyList<SubtitleCue> full = SequenceSubtitleMapper.Map(document, cues);
        Assert.Equal(4, full.Count);
        Assert.Equal([(0d, 2d), (8d, 10d), (10d, 12d), (18d, 20d)], full.Select(cue => (cue.StartSeconds, cue.EndSeconds)));
        IReadOnlyList<SubtitleCue> selected = SequenceSubtitleMapper.Map(document, cues, selectedId);
        Assert.Equal([(0d, 2d), (8d, 10d)], selected.Select(cue => (cue.StartSeconds, cue.EndSeconds)));
        Assert.Equal(["First", "Last"], selected.Select(cue => cue.Text));
        Assert.Throws<InvalidOperationException>(() => SequenceSubtitleMapper.Map(document, new Dictionary<Guid, IReadOnlyList<SubtitleCue>>()));
    }

    [Fact]
    public void History_restores_edits_and_discards_redo_after_a_new_edit()
    {
        Guid projectId = Guid.NewGuid();
        var source = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 20, 1920, 1080);
        Guid placementId = Guid.NewGuid();
        SequenceDocument document = SequenceEditor.AddSource(SequenceDocument.Create(projectId), source);
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(placementId, source.Id, 0, 10, new SequenceCrop()));
        var history = new SequenceEditHistory(document);

        history.Apply(current => SequenceEditor.Trim(current, placementId, 2, 8));
        Assert.True(history.Undo());
        Assert.Equal(0, Assert.Single(history.Current.Placements).SourceInSeconds);
        Assert.True(history.Redo());
        Assert.Equal(2, Assert.Single(history.Current.Placements).SourceInSeconds);
        Assert.True(history.Undo());
        history.Apply(current => SequenceEditor.SetFrame(current, new SequenceFrame(1080, 1920)));
        Assert.False(history.CanRedo);
        Assert.False(history.Redo());
        Assert.Equal(new SequenceFrame(1080, 1920), history.Current.Frame);
    }

    [Fact]
    public void Duplicate_placements_trim_split_move_and_rebase_without_changing_source_times()
    {
        Guid projectId = Guid.NewGuid();
        var source = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 120, 1920, 1080);
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Guid splitId = Guid.NewGuid();
        SequenceDocument document = SequenceDocument.Create(projectId);
        document = SequenceEditor.AddSource(document, source);
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(firstId, source.Id, 10, 30, new SequenceCrop()));
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(secondId, source.Id, 50, 60, new SequenceCrop()));
        Assert.Equal(new SequenceFrame(1920, 1080), document.Frame);

        document = SequenceEditor.Trim(document, firstId, 12, 28);
        document = SequenceEditor.SetCrop(document, firstId, new SequenceCrop(2, 0.25, 0.75));
        document = SequenceEditor.Split(document, firstId, 8, splitId);
        document = SequenceEditor.Move(document, secondId, 0);
        IReadOnlyList<SequencePlacementPosition> positions = SequenceEditor.GetPositions(document);

        Assert.Equal([secondId, firstId, splitId], positions.Select(position => position.Placement.Id));
        Assert.Equal((0d, 10d), (positions[0].SequenceStartSeconds, positions[0].SequenceEndSeconds));
        Assert.Equal((10d, 18d), (positions[1].SequenceStartSeconds, positions[1].SequenceEndSeconds));
        Assert.Equal((12d, 20d), (positions[1].Placement.SourceInSeconds, positions[1].Placement.SourceOutSeconds));
        Assert.Equal((20d, 28d), (positions[2].Placement.SourceInSeconds, positions[2].Placement.SourceOutSeconds));
        Assert.Equal(new SequenceCrop(2, 0.25, 0.75), positions[2].Placement.Crop);
        Assert.Equal(new SequenceRange(10, 14), SequenceEditor.RebaseRange(positions[1], 9, 16));
        Assert.Equal(new SequenceRange(16, 18), SequenceEditor.RebaseRange(positions[1], 18, 22));
        Assert.Null(SequenceEditor.RebaseRange(positions[1], 20, 22));
        Assert.Equal(new SequenceRange(0, 4), SequenceEditor.RebaseRange(
            positions[1] with { SequenceStartSeconds = 0 }, 9, 16));
        Assert.Equal(120, source.DurationSeconds);
    }

    [Fact]
    public void Invalid_sources_and_placements_cannot_be_added()
    {
        Guid projectId = Guid.NewGuid();
        var source = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 10, 800, 600);
        SequenceDocument document = SequenceEditor.AddSource(SequenceDocument.Create(projectId), source);
        Assert.Throws<InvalidDataException>(() => SequenceEditor.AddSource(document, source));
        Assert.Throws<InvalidDataException>(() => SequenceEditor.AddSource(document,
            source with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), WorkspaceRelativePath = "../outside" }));
        Assert.Throws<InvalidDataException>(() => SequenceEditor.AddSource(document,
            source with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), WorkspaceRelativePath = "sources/CON.trackdub" }));
        Assert.Throws<InvalidDataException>(() => SequenceEditor.AddSource(document,
            source with { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), WorkspaceRelativePath = "sources/chi\0ld.trackdub" }));
        Assert.Throws<InvalidDataException>(() => SequenceEditor.AddPlacement(document,
            new SequencePlacement(Guid.NewGuid(), source.Id, 0, 11, new SequenceCrop())));
        Assert.Throws<InvalidDataException>(() => SequenceEditor.AddPlacement(document,
            new SequencePlacement(Guid.NewGuid(), source.Id, 0, 1, new SequenceCrop(FocusX: double.NaN))));
        Assert.Throws<InvalidOperationException>(() => SequenceEditor.AddPlacement(document,
            new SequencePlacement(Guid.NewGuid(), Guid.NewGuid(), 0, 1, new SequenceCrop())));
    }

    [Fact]
    public void Explicit_frame_is_shared_when_original_first_clip_is_removed()
    {
        Guid projectId = Guid.NewGuid();
        var source = new SequenceSource(Guid.NewGuid(), projectId, Guid.NewGuid(), null, 20, 1920, 1080);
        Guid firstId = Guid.NewGuid();
        SequenceDocument document = SequenceEditor.AddSource(SequenceDocument.Create(projectId), source);
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(firstId, source.Id, 0, 10, new SequenceCrop()));
        document = SequenceEditor.SetFrame(document, new SequenceFrame(1080, 1920));
        document = SequenceEditor.Remove(document, firstId);
        document = SequenceEditor.AddPlacement(document, new SequencePlacement(Guid.NewGuid(), source.Id, 10, 20, new SequenceCrop()));
        Assert.Equal(new SequenceFrame(1080, 1920), document.Frame);
    }
}
