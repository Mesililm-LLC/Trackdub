using Trackdub.Domain.Editing;

namespace Trackdub.Application.Editing;

/// <summary>In-memory undo history scoped to a single sequence editing session.</summary>
public sealed class SequenceEditHistory
{
    private readonly Stack<SequenceDocument> undo = new();
    private readonly Stack<SequenceDocument> redo = new();

    public SequenceEditHistory(SequenceDocument initial)
    {
        SequenceEditor.Validate(initial);
        Current = Snapshot(initial);
    }

    public SequenceDocument Current { get; private set; }

    public bool CanUndo => undo.Count > 0;

    public bool CanRedo => redo.Count > 0;

    public void Apply(Func<SequenceDocument, SequenceDocument> edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        SequenceDocument next = edit(Snapshot(Current));
        SequenceEditor.Validate(next);
        if (next.ProjectId != Current.ProjectId)
        {
            throw new InvalidOperationException("An edit cannot change the sequence project.");
        }
        undo.Push(Current);
        Current = Snapshot(next);
        redo.Clear();
    }

    public bool Undo()
    {
        if (!undo.TryPop(out SequenceDocument? previous))
        {
            return false;
        }
        redo.Push(Current);
        Current = previous;
        return true;
    }

    public bool Redo()
    {
        if (!redo.TryPop(out SequenceDocument? next))
        {
            return false;
        }
        undo.Push(Current);
        Current = next;
        return true;
    }

    private static SequenceDocument Snapshot(SequenceDocument document) => document with
    {
        Sources = Array.AsReadOnly(document.Sources.ToArray()),
        Placements = Array.AsReadOnly(document.Placements.ToArray()),
    };
}
