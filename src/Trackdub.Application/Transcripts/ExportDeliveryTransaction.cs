using System.Text.Json;

namespace Trackdub.Application.Transcripts;

/// <summary>Owns one export family until its producer has been accepted by the stage-run store.</summary>
internal sealed class ExportDeliveryTransaction : IDisposable
{
    private const int JournalVersion = 1;
    private readonly (string DestinationPath, string ReplacementPath)[] replacements;
    private readonly string outputPath;
    private Journal journal;
    private FileStream? transactionLock;

    public ExportDeliveryTransaction(
        IEnumerable<(string DestinationPath, string ReplacementPath)> replacements,
        Guid producerStageRunId)
    {
        this.replacements = replacements.ToArray();
        if (this.replacements.Length == 0 || producerStageRunId == Guid.Empty)
            throw new ArgumentException("An export delivery requires outputs and a producer stage-run ID.");
        outputPath = Path.GetFullPath(this.replacements[0].DestinationPath);
        journal = new Journal(JournalVersion, outputPath, producerStageRunId, false, false,
            this.replacements.Select(pair =>
            {
                string destination = Path.GetFullPath(pair.DestinationPath);
                return new Entry(destination, AdjacentPath(destination, producerStageRunId, ".tmp"),
                    AdjacentPath(destination, producerStageRunId, ".bak"), File.Exists(destination));
            }).ToArray());
        Validate(journal, outputPath);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        transactionLock = AcquireLock(outputPath);
        try
        {
            RecoverUnderLock(outputPath);
            journal = journal with
            {
                PreviousAcceptedDelivery = GetAcceptedDelivery(ReadJournal(outputPath)),
                Entries = journal.Entries.Select(entry => entry with
                { HadExistingFile = File.Exists(entry.DestinationPath) }).ToArray()
            };
            for (int index = 0; index < replacements.Length; index++)
            {
                Entry entry = journal.Entries[index];
                Directory.CreateDirectory(Path.GetDirectoryName(entry.DestinationPath)!);
                await using FileStream source = File.OpenRead(replacements[index].ReplacementPath);
                await using var temporary = new FileStream(entry.TemporaryPath, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await source.CopyToAsync(temporary, cancellationToken).ConfigureAwait(false);
                temporary.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            WriteJournal(journal);
        }
        catch
        {
            try
            {
                foreach (Entry entry in journal.Entries) TryDelete(entry.TemporaryPath);
            }
            finally { Dispose(); }
            throw;
        }
    }

    public void Apply()
    {
        EnsureLockHeld();
        // Prepare persisted every recovery path before the first destination is touched.
        Journal prepared = ReadJournal(outputPath) ?? throw new InvalidOperationException("Delivery has not been prepared.");
        if (prepared.ProducerStageRunId != journal.ProducerStageRunId || prepared.Accepted || prepared.RolledBack)
            throw new InvalidOperationException("Delivery transaction is no longer pending.");
        foreach (Entry entry in journal.Entries)
        {
            if (entry.HadExistingFile) File.Move(entry.DestinationPath, entry.BackupPath);
            File.Move(entry.TemporaryPath, entry.DestinationPath);
        }
    }

    public void Accept()
    {
        EnsureLockHeld();
        journal = journal with { Accepted = true, PreviousAcceptedDelivery = null };
        WriteJournal(journal);
        Cleanup(journal);
        Dispose();
    }

    public void Rollback()
    {
        if (transactionLock is null) return;
        try { RecoverUnderLock(outputPath); }
        finally { Dispose(); }
    }

    // Releasing the handle leaves unaccepted durable work for the next owner to recover.
    public void Dispose()
    {
        transactionLock?.Dispose();
        transactionLock = null;
    }

    public static void Recover(string outputPath)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        if (!File.Exists(JournalPath(fullOutputPath))) return;
        using FileStream recoveryLock = AcquireLock(fullOutputPath);
        RecoverUnderLock(fullOutputPath);
    }

    private static void RecoverUnderLock(string fullOutputPath)
    {
        Journal? journal = ReadJournal(fullOutputPath);
        if (journal is null) return;
        if (!journal.Accepted && !journal.RolledBack)
        {
            foreach (Entry entry in journal.Entries.Reverse())
            {
                if (File.Exists(entry.BackupPath))
                {
                    // Retain the backup until the entire rollback is durable, so interruption of
                    // recovery itself can safely retry every member of the delivery family.
                    File.Copy(entry.BackupPath, entry.TemporaryPath, overwrite: true);
                    using (var restored = new FileStream(entry.TemporaryPath, FileMode.Open, FileAccess.Write))
                        restored.Flush(flushToDisk: true);
                    File.Move(entry.TemporaryPath, entry.DestinationPath, overwrite: true);
                }
                else if (!entry.HadExistingFile && !File.Exists(entry.TemporaryPath))
                {
                    DeleteIfPresent(entry.DestinationPath);
                }
            }
            journal = journal with { RolledBack = true };
            WriteJournal(journal);
        }
        Cleanup(journal);
    }

    private static FileStream AcquireLock(string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        // A retained lock file is harmless. Deleting it would allow another process to lock a
        // different inode on Unix while the original owner still holds the old one.
        return new FileStream(JournalPath(outputPath) + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private void EnsureLockHeld()
    {
        if (transactionLock is null) throw new InvalidOperationException("Delivery transaction does not hold the output lock.");
    }

    /// <summary>Legacy exports have no journal; journaled deliveries must match the completed producer.</summary>
    internal static bool IsAcceptedDelivery(string outputPath, Guid producerStageRunId)
    {
        string fullOutputPath = Path.GetFullPath(outputPath);
        string journalPath = JournalPath(fullOutputPath);
        if (!File.Exists(journalPath))
        {
            // No journal and no writer: legacy acceptance is purely whether the output
            // exists. Probe without creating any metadata so read-only locations work.
            string lockPath = journalPath + ".lock";
            if (!File.Exists(lockPath)) return File.Exists(fullOutputPath);
            // Legacy acceptance needs no writable metadata. An existing writer lock still
            // has to be honored, including while it is staging before its journal exists.
            using var legacyLock = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            if (!File.Exists(journalPath)) return File.Exists(fullOutputPath);
        }
        using FileStream recoveryLock = AcquireLock(fullOutputPath);
        RecoverUnderLock(fullOutputPath);
        Journal? journal = ReadJournal(fullOutputPath);
        AcceptedDelivery? accepted = GetAcceptedDelivery(journal);
        return journal is null || (accepted is not null && accepted.ProducerStageRunId == producerStageRunId &&
            FilePathComparison.AreSame(accepted.OutputPath, fullOutputPath));
    }

    private static AcceptedDelivery? GetAcceptedDelivery(Journal? journal) =>
        journal?.Accepted == true ? new AcceptedDelivery(journal.OutputPath, journal.ProducerStageRunId)
            : journal?.RolledBack == true ? journal.PreviousAcceptedDelivery : null;

    private static Journal? ReadJournal(string outputPath)
    {
        string path = JournalPath(outputPath);
        if (!File.Exists(path)) return null;
        Journal journal = JsonSerializer.Deserialize<Journal>(File.ReadAllText(path))
            ?? throw new InvalidDataException("Export delivery recovery journal is empty.");
        try
        {
            // Validate the original record before considering relocation. A pending or
            // malformed foreign journal must never authorize changes at either location.
            Validate(journal, journal.OutputPath);
            if (!FilePathComparison.AreSame(JournalPath(journal.OutputPath), path))
            {
                // A pending journal must never authorize changes at a foreign location.
                if (!journal.Accepted && !journal.RolledBack)
                    throw new InvalidDataException("Pending export delivery recovery journal belongs to another output directory.");
                // A terminal journal records no pending work: a file-name mismatch means the
                // delivery family moved on, so there is no journal here. Leave its files alone.
                if (!string.Equals(Path.GetFileName(JournalPath(journal.OutputPath)), Path.GetFileName(path),
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    return null;
                string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
                string Relocate(string previousPath) => Path.Join(directory, Path.GetFileName(previousPath));
                journal = journal with
                {
                    OutputPath = Relocate(journal.OutputPath),
                    Entries = journal.Entries.Select(entry => entry with
                    {
                        DestinationPath = Relocate(entry.DestinationPath),
                        TemporaryPath = Relocate(entry.TemporaryPath),
                        BackupPath = Relocate(entry.BackupPath)
                    }).ToArray(),
                    PreviousAcceptedDelivery = journal.PreviousAcceptedDelivery is AcceptedDelivery previous
                        ? previous with { OutputPath = Relocate(previous.OutputPath) } : null
                };
            }
            Validate(journal, outputPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("Export delivery recovery journal contains an invalid path.", exception);
        }
        return journal;
    }

    private static void WriteJournal(Journal journal)
    {
        string path = JournalPath(journal.OutputPath);
        string temporaryPath = path + ".tmp";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write,
                   FileShare.None, 4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, journal);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static void Validate(Journal journal, string outputPath)
    {
        if (journal.Version != JournalVersion || journal.ProducerStageRunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(journal.OutputPath) || !Path.IsPathFullyQualified(journal.OutputPath) ||
            !FilePathComparison.AreSame(JournalPath(journal.OutputPath), JournalPath(outputPath)) ||
            journal.Entries is not { Length: > 0 } ||
            (journal.Accepted && journal.RolledBack))
            throw new InvalidDataException("Invalid export delivery recovery journal.");
        string ownerOutputPath = journal.OutputPath;
        string extension = Path.GetExtension(ownerOutputPath);
        if (!string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".mkv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Export delivery recovery journal contains an unsupported output container.");
        if (journal.PreviousAcceptedDelivery is AcceptedDelivery previous &&
            (previous.ProducerStageRunId == Guid.Empty || string.IsNullOrWhiteSpace(previous.OutputPath) ||
             !Path.IsPathFullyQualified(previous.OutputPath) ||
             !FilePathComparison.AreSame(JournalPath(previous.OutputPath), JournalPath(ownerOutputPath)) ||
             (!string.Equals(Path.GetExtension(previous.OutputPath), ".mp4", StringComparison.OrdinalIgnoreCase) &&
              !string.Equals(Path.GetExtension(previous.OutputPath), ".mkv", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Export delivery recovery journal contains an invalid previous acceptance identity.");
        string[] family = [ownerOutputPath, Path.ChangeExtension(ownerOutputPath, ".srt"), Path.ChangeExtension(ownerOutputPath, ".vtt"),
            Path.ChangeExtension(ownerOutputPath, ".ass"), Path.ChangeExtension(ownerOutputPath, ".export-manifest.json")];
        var destinations = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (Entry entry in journal.Entries)
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.DestinationPath) ||
                string.IsNullOrWhiteSpace(entry.TemporaryPath) || string.IsNullOrWhiteSpace(entry.BackupPath) ||
                !Path.IsPathFullyQualified(entry.DestinationPath) ||
                !Path.IsPathFullyQualified(entry.TemporaryPath) || !Path.IsPathFullyQualified(entry.BackupPath) ||
                !family.Any(path => FilePathComparison.AreSame(path, entry.DestinationPath)) ||
                !destinations.Add(Path.GetFullPath(entry.DestinationPath)) ||
                !FilePathComparison.AreSame(entry.TemporaryPath, AdjacentPath(entry.DestinationPath, journal.ProducerStageRunId, ".tmp")) ||
                !FilePathComparison.AreSame(entry.BackupPath, AdjacentPath(entry.DestinationPath, journal.ProducerStageRunId, ".bak")))
                throw new InvalidDataException("Export delivery recovery paths are outside the output family.");
        }
        if (!FilePathComparison.AreSame(journal.Entries[0].DestinationPath, ownerOutputPath))
            throw new InvalidDataException("Export delivery recovery journal does not own the requested output.");
    }

    private static string JournalPath(string outputPath) =>
        // MP4 and MKV with the same stem own identical manifest/subtitle paths. They must
        // recover and replace a single durable owner under the same lock.
        Path.Join(Path.GetDirectoryName(outputPath), $".{Path.GetFileNameWithoutExtension(outputPath)}.export-delivery.json");

    private static string AdjacentPath(string destination, Guid producer, string suffix) =>
        Path.Join(Path.GetDirectoryName(destination), $".{Path.GetFileName(destination)}.{producer:N}{suffix}");

    private static void Cleanup(Journal journal)
    {
        // The terminal journal stays as the durable delivery identity for resume. Never enumerate
        // or remove unknown backups: only this acknowledged transaction's exact files are ours.
        foreach (Entry entry in journal.Entries)
        {
            TryDelete(entry.TemporaryPath);
            TryDelete(entry.BackupPath);
        }
    }

    private static void TryDelete(string path)
    {
        try { DeleteIfPresent(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine(
                $"Skipping best-effort export delivery cleanup for '{path}': {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private sealed record Journal(int Version, string OutputPath, Guid ProducerStageRunId,
        bool Accepted, bool RolledBack, Entry[] Entries, AcceptedDelivery? PreviousAcceptedDelivery = null);

    private sealed record AcceptedDelivery(string OutputPath, Guid ProducerStageRunId);

    private sealed record Entry(string DestinationPath, string TemporaryPath, string BackupPath, bool HadExistingFile);
}
