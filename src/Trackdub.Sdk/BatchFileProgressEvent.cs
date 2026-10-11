namespace Trackdub.Sdk;

/// <summary>
/// Identifies the kind of per-file boundary emitted by <see cref="BatchProcessor"/>.
/// </summary>
public enum BatchFileProgressKind
{
    /// <summary>A file is about to be attempted.</summary>
    Started = 0,

    /// <summary>A file completed successfully.</summary>
    Succeeded = 1,

    /// <summary>A file failed, including before the dubbing engine ran.</summary>
    Failed = 2,

    /// <summary>A file was skipped because an earlier file failed in fail-fast mode.</summary>
    Skipped = 3,
}

/// <summary>
/// Per-file boundary emitted by <see cref="BatchProcessor"/> as a batch advances.
/// Start and outcome boundaries are emitted for every attempted file, including files that
/// fail before <see cref="Contracts.Pipeline"/> engine execution begins.
/// </summary>
public sealed record BatchFileProgressEvent
{
    /// <summary>Zero-based index of the file in the batch.</summary>
    public required int FileIndex { get; init; }

    /// <summary>Full path to the media file.</summary>
    public required string FilePath { get; init; }

    /// <summary>Kind of boundary represented by this event.</summary>
    public required BatchFileProgressKind Kind { get; init; }

    /// <summary>Diagnostic reason for <see cref="BatchFileProgressKind.Failed"/> or
    /// <see cref="BatchFileProgressKind.Skipped"/> boundaries.</summary>
    public string? Reason { get; init; }
}
