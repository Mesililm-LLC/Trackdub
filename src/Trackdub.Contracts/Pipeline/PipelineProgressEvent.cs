using System.Text.Json.Serialization;

namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Identifies the kind of progress event emitted during pipeline execution.
/// </summary>
public enum PipelineProgressEventKind
{
    /// <summary>A pipeline stage has started execution.</summary>
    Started = 0,

    /// <summary>A pipeline stage reports incremental progress.</summary>
    Progress = 1,

    /// <summary>A pipeline stage completed successfully.</summary>
    Completed = 2,

    /// <summary>A pipeline stage failed with an error.</summary>
    Failed = 3,

    /// <summary>A pipeline stage was skipped.</summary>
    Skipped = 4
}

/// <summary>
/// Identifies the kind of pipeline output that truthfully exists. Output events keep
/// <see cref="PipelineProgressEventKind.Progress"/> and are distinguished structurally
/// by <see cref="PipelineProgressEvent.OutputKind"/>.
/// </summary>
public enum PipelineOutputKind
{
    /// <summary>The first recognized transcript segment exists in memory.</summary>
    TranscriptSegmentAvailable = 0,

    /// <summary>A transcript segment was persisted: repository save and artifact write succeeded.</summary>
    TranscriptSegmentPersisted = 1,

    /// <summary>Playable dubbed audio for a segment was persisted (artifact and store committed).</summary>
    PlayableAudioPersisted = 2
}

/// <summary>
/// A structured progress event emitted during pipeline execution.
/// </summary>
public sealed record PipelineProgressEvent
{
    public PipelineProgressEvent(
        string StageName,
        PipelineProgressEventKind EventKind,
        double Percentage,
        string? Message,
        TimeSpan ElapsedDuration)
        : this(
            StageName,
            EventKind,
            Percentage,
            Message,
            ElapsedDuration,
            StageKey: null,
            Phase: null,
            CompletedUnits: null,
            TotalUnits: null,
            CurrentItemLabel: null)
    {
    }

    [JsonConstructor]
    public PipelineProgressEvent(
        string StageName,
        PipelineProgressEventKind EventKind,
        double? PercentComplete = null,
        string? Message = null,
        TimeSpan ElapsedDuration = default,
        string? StageKey = null,
        string? Phase = null,
        int? CompletedUnits = null,
        int? TotalUnits = null,
        string? CurrentItemLabel = null,
        Guid? RunId = null,
        long SequenceNumber = 0,
        PipelineOutputKind? OutputKind = null,
        int? ItemIndex = null,
        Guid? RevisionId = null,
        Guid? SegmentId = null,
        Guid? ArtifactId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(StageName);

        this.StageName = StageName;
        this.EventKind = EventKind;
        this.PercentComplete = PercentComplete is double value
            ? Math.Clamp(value, 0d, 100d)
            : null;
        this.Message = Message;
        this.ElapsedDuration = ElapsedDuration;
        this.StageKey = string.IsNullOrWhiteSpace(StageKey) ? StageName : StageKey;
        this.Phase = Phase;
        this.CompletedUnits = CompletedUnits;
        this.TotalUnits = TotalUnits;
        this.CurrentItemLabel = CurrentItemLabel;
        this.RunId = RunId;
        this.SequenceNumber = SequenceNumber;
        this.OutputKind = OutputKind;
        this.ItemIndex = ItemIndex;
        this.RevisionId = RevisionId;
        this.SegmentId = SegmentId;
        this.ArtifactId = ArtifactId;
    }

    /// <summary>The user-facing name of the pipeline stage.</summary>
    public string StageName { get; init; }

    /// <summary>The stable pipeline stage key when it differs from <see cref="StageName"/>.</summary>
    public string StageKey { get; init; }

    /// <summary>The kind of progress event.</summary>
    public PipelineProgressEventKind EventKind { get; init; }

    /// <summary>Progress percentage from 0 to 100, or null when the workflow cannot know it honestly.</summary>
    public double? PercentComplete { get; init; }

    /// <summary>Compatibility value for existing callers that expect a non-null percentage.</summary>
    public double Percentage => PercentComplete ?? 0d;

    /// <summary>Optional machine-readable or concise phase name.</summary>
    public string? Phase { get; init; }

    /// <summary>Optional completed unit count for determinate work.</summary>
    public int? CompletedUnits { get; init; }

    /// <summary>Optional total unit count for determinate work.</summary>
    public int? TotalUnits { get; init; }

    /// <summary>Optional label for the current item being processed.</summary>
    public string? CurrentItemLabel { get; init; }

    /// <summary>Optional human-readable message providing additional context.</summary>
    public string? Message { get; init; }

    /// <summary>Elapsed time for terminal events.</summary>
    public TimeSpan ElapsedDuration { get; init; }

    /// <summary>Identity of the engine run that emitted this event, stamped by the run-scoped reporter.</summary>
    public Guid? RunId { get; init; }

    /// <summary>Monotonic sequence number within <see cref="RunId"/>; strictly increasing per run.</summary>
    public long SequenceNumber { get; init; }

    /// <summary>When set, this <see cref="PipelineProgressEventKind.Progress"/> event reports a pipeline output that truthfully exists.</summary>
    public PipelineOutputKind? OutputKind { get; init; }

    /// <summary>Zero-based item index the output refers to (e.g. segment index).</summary>
    public int? ItemIndex { get; init; }

    /// <summary>Transcript revision the output belongs to, when persisted.</summary>
    public Guid? RevisionId { get; init; }

    /// <summary>Persisted segment identity, when the output names a stored segment.</summary>
    public Guid? SegmentId { get; init; }

    /// <summary>Persisted artifact identity, when the output names a committed artifact.</summary>
    public Guid? ArtifactId { get; init; }
}
