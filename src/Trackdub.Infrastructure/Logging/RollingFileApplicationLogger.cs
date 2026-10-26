using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Logging;

public enum ApplicationLogLevel
{
    Debug = 0,
    Information = 1,
    Warning = 2,
    Error = 3
}

/// <summary>
/// Writes troubleshooting application logs to a bounded rolling file under the local app data root.
/// </summary>
public sealed class RollingFileApplicationLogger : IApplicationLogger, IDisposable
{
    public const long DefaultMaxFileBytes = 1 * 1024 * 1024;
    public const int DefaultMaxArchiveFiles = 3;
    public const int SessionMaxArchiveFiles = 10;
    public const int DefaultMaxEntryCharacters = 64 * 1024;
    private const int MaxQueuedEntries = 1024;
    private static readonly TimeSpan DisposeFlushTimeout = TimeSpan.FromSeconds(5);

    private readonly object syncRoot = new();
    private readonly object queueSyncRoot = new();
    private readonly string logFilePath;
    private readonly long maxFileBytes;
    private readonly int maxArchiveFiles;
    private readonly int maxEntryCharacters;
    private readonly ApplicationLogLevel minimumLevel;
    private readonly BlockingCollection<string> pendingEntries = new(MaxQueuedEntries);
    private readonly Task writerTask;
    private bool archivesPruned;
    private int disposed;
    private long enqueuedEntries;
    private long settledEntries;
    private long writtenEntries;

    public RollingFileApplicationLogger(TrackdubStoragePaths storagePaths)
        : this(storagePaths.LogFilePath)
    {
    }

    public RollingFileApplicationLogger(
        TrackdubStoragePaths storagePaths,
        ApplicationLogLevel minimumLevel)
        : this(storagePaths.LogFilePath, minimumLevel: minimumLevel)
    {
    }

    public RollingFileApplicationLogger(
        string logFilePath,
        long maxFileBytes = DefaultMaxFileBytes,
        int maxArchiveFiles = DefaultMaxArchiveFiles,
        int maxEntryCharacters = DefaultMaxEntryCharacters,
        ApplicationLogLevel minimumLevel = ApplicationLogLevel.Warning,
        bool rotateOnStartup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxArchiveFiles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntryCharacters);

        this.logFilePath = logFilePath;
        this.maxFileBytes = maxFileBytes;
        this.maxArchiveFiles = maxArchiveFiles;
        this.maxEntryCharacters = maxEntryCharacters;
        this.minimumLevel = minimumLevel;

        if (rotateOnStartup)
        {
            RotateOnStartup();
        }

        writerTask = Task.Factory.StartNew(
            ProcessPendingEntries,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public string LogFilePath => logFilePath;

    public long MaxFileBytes => maxFileBytes;

    public int MaxArchiveFiles => maxArchiveFiles;

    public ApplicationLogLevel MinimumLevel => minimumLevel;

    public void LogDebug(string message) => Write(ApplicationLogLevel.Debug, "DEBUG", message, exception: null);

    public void LogInformation(string message) => Write(ApplicationLogLevel.Information, "INFO", message, exception: null);

    public void LogWarning(string message, Exception? exception = null) =>
        Write(ApplicationLogLevel.Warning, "WARN", message, exception);

    public void LogError(string message, Exception? exception = null) =>
        Write(ApplicationLogLevel.Error, "ERROR", message, exception);

    public void LogErrorSynchronously(string message, Exception? exception = null)
    {
        if (ApplicationLogLevel.Error < minimumLevel)
        {
            return;
        }

        string entry = BuildEntry("ERROR", message, exception);
        Debug.Write(entry);
        if (!WriteEntryToFile(entry))
        {
            throw new IOException($"Failed to write Trackdub log entry to '{logFilePath}'.");
        }
    }

    public void Flush() => Flush(DisposeFlushTimeout);

    public void Flush(TimeSpan timeout)
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        if (timeout < TimeSpan.Zero)
        {
            timeout = TimeSpan.Zero;
        }

        // Drain by waiting until every entry enqueued before this call has been settled
        // by the writer (attempted write completed). Successful disk writes are tracked
        // separately; waiting only on successes would permanently stall Flush after one
        // IO failure. Snapshot under queueSyncRoot so we cannot miss an entry that has
        // already been admitted via TryAdd but whose counter increment is in flight.
        long targetSettled;
        lock (queueSyncRoot)
        {
            targetSettled = enqueuedEntries;
        }

        if (Volatile.Read(ref settledEntries) >= targetSettled)
        {
            return;
        }

        TimeSpan effectiveTimeout = timeout > DisposeFlushTimeout ? DisposeFlushTimeout : timeout;
        var deadline = DateTime.UtcNow + effectiveTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Volatile.Read(ref settledEntries) >= targetSettled)
            {
                return;
            }

            Thread.Sleep(25);
        }

        if (Volatile.Read(ref settledEntries) >= targetSettled)
        {
            return;
        }

        Debug.WriteLine(
            $"[WARN] Timed out flushing Trackdub log file '{logFilePath}' after {effectiveTimeout.TotalSeconds:F1}s " +
            $"(settled={Volatile.Read(ref settledEntries)}, written={Volatile.Read(ref writtenEntries)}, target={targetSettled}).");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        pendingEntries.CompleteAdding();
        try
        {
            if (!writerTask.Wait(DisposeFlushTimeout))
            {
                Debug.WriteLine(
                    $"[WARN] Timed out flushing Trackdub log file '{logFilePath}' after {DisposeFlushTimeout.TotalSeconds:F1}s.");
                _ = writerTask.ContinueWith(
                    static (_, state) => ((BlockingCollection<string>)state!).Dispose(),
                    pendingEntries,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or AggregateException)
        {
            Debug.WriteLine($"[WARN] Failed to flush Trackdub log file '{logFilePath}': {ex.Message}");
        }

        pendingEntries.Dispose();
    }

    private void Write(ApplicationLogLevel entryLevel, string level, string message, Exception? exception)
    {
        if (entryLevel < minimumLevel)
        {
            return;
        }

        string entry = BuildEntry(level, message, exception);
        Debug.Write(entry);

        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        try
        {
            lock (queueSyncRoot)
            {
                if (!pendingEntries.TryAdd(entry))
                {
                    Debug.WriteLine($"[WARN] Dropped Trackdub log entry because the queue is full for '{logFilePath}'.");
                }
                else
                {
                    enqueuedEntries++;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            Debug.WriteLine($"[WARN] Failed to queue Trackdub log entry for '{logFilePath}': {ex.Message}");
        }
    }

    private void ProcessPendingEntries()
    {
        foreach (string entry in pendingEntries.GetConsumingEnumerable())
        {
            try
            {
                if (WriteEntryToFile(entry))
                {
                    Interlocked.Increment(ref writtenEntries);
                }
            }
            catch (Exception ex) when (
                ex is IOException
                    or UnauthorizedAccessException
                    or NotSupportedException
                    or ArgumentException)
            {
                // Recoverable path/append failures must not kill the writer loop.
                Debug.WriteLine($"[WARN] Failed to write Trackdub log file '{logFilePath}': {ex.Message}");
            }
            finally
            {
                Interlocked.Increment(ref settledEntries);
            }
        }
    }

    private bool WriteEntryToFile(string entry)
    {
        try
        {
            lock (syncRoot)
            {
                string? directory = Path.GetDirectoryName(logFilePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                PruneArchiveFilesOnce();
                RotateIfNeeded(Encoding.UTF8.GetByteCount(entry));
                AppendShared(logFilePath, entry);
            }

            return true;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            Debug.WriteLine($"[WARN] Failed to write Trackdub log file '{logFilePath}': {ex.Message}");
            return false;
        }
    }

    private string BuildEntry(string level, string message, Exception? exception)
    {
        var builder = new StringBuilder();
        builder
            .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
            .Append(' ')
            .Append('[')
            .Append(level)
            .Append("] ")
            .Append("[pid:")
            .Append(Environment.ProcessId.ToString(CultureInfo.InvariantCulture))
            .Append(" tid:")
            .Append(Environment.CurrentManagedThreadId.ToString(CultureInfo.InvariantCulture))
            .Append("] ")
            .Append(message);

        if (exception is not null)
        {
            builder.AppendLine();
            builder.Append(exception);
        }

        string entry = builder.ToString();
        if (entry.Length > maxEntryCharacters)
        {
            entry = string.Concat(
                entry[..maxEntryCharacters],
                Environment.NewLine,
                "[TRUNCATED] Log entry exceeded ",
                maxEntryCharacters.ToString(CultureInfo.InvariantCulture),
                " characters.");
        }

        return string.Concat(entry, Environment.NewLine);
    }

    private void RotateIfNeeded(long pendingBytes)
    {
        var logFile = new FileInfo(logFilePath);
        if (!logFile.Exists || logFile.Length + pendingBytes <= maxFileBytes)
        {
            return;
        }

        if (maxArchiveFiles == 0)
        {
            logFile.Delete();
            return;
        }

        string oldestArchive = GetArchivePath(maxArchiveFiles);
        if (File.Exists(oldestArchive))
        {
            File.Delete(oldestArchive);
        }

        for (int archiveIndex = maxArchiveFiles - 1; archiveIndex >= 1; archiveIndex--)
        {
            string sourcePath = GetArchivePath(archiveIndex);
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            File.Move(sourcePath, GetArchivePath(archiveIndex + 1), overwrite: true);
        }

        File.Move(logFilePath, GetArchivePath(1), overwrite: true);
    }

    private void PruneArchiveFiles()
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(logFilePath)) ?? Directory.GetCurrentDirectory();
        if (!Directory.Exists(directory))
        {
            return;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(logFilePath);
        string extension = Path.GetExtension(logFilePath);
        string searchPattern = string.Concat(fileNameWithoutExtension, ".*", extension);

        foreach (string archivePath in Directory.EnumerateFiles(directory, searchPattern))
        {
            string archiveName = Path.GetFileNameWithoutExtension(archivePath);
            string archiveIndexText = archiveName[(fileNameWithoutExtension.Length + 1)..];
            if (int.TryParse(archiveIndexText, NumberStyles.None, CultureInfo.InvariantCulture, out int archiveIndex) &&
                archiveIndex > maxArchiveFiles)
            {
                File.Delete(archivePath);
            }
        }
    }

    private void PruneArchiveFilesOnce()
    {
        if (archivesPruned)
        {
            return;
        }

        PruneArchiveFiles();
        archivesPruned = true;
    }

    /// <summary>
    /// Rotates the current log file into the first archive slot on startup, so each application
    /// session starts with a fresh log file. Older archive files are shifted and pruned to keep
    /// at most <see cref="maxArchiveFiles"/> session archives.
    /// </summary>
    private void RotateOnStartup()
    {
        try
        {
            if (!File.Exists(logFilePath))
            {
                PruneArchiveFiles();
                archivesPruned = true;
                return;
            }

            // Prune archives that exceed the limit before shifting.
            PruneArchiveFiles();
            archivesPruned = true;

            if (maxArchiveFiles == 0)
            {
                File.Delete(logFilePath);
                return;
            }

            // Shift existing archives: trackdub.N.log → trackdub.(N+1).log
            string oldestArchive = GetArchivePath(maxArchiveFiles);
            if (File.Exists(oldestArchive))
            {
                File.Delete(oldestArchive);
            }

            for (int archiveIndex = maxArchiveFiles - 1; archiveIndex >= 1; archiveIndex--)
            {
                string sourcePath = GetArchivePath(archiveIndex);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                File.Move(sourcePath, GetArchivePath(archiveIndex + 1), overwrite: true);
            }

            File.Move(logFilePath, GetArchivePath(1), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Debug.WriteLine($"[WARN] Failed to rotate Trackdub session log '{logFilePath}' on startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Appends <paramref name="entry"/> allowing other writers and readers to hold the file open.
    /// </summary>
    /// <remarks>
    /// <see cref="File.AppendAllText(string,string,Encoding)"/> opens with
    /// <c>FileShare.Read</c>, so two loggers pointed at the same path (the CLI and a host, or
    /// several test factories sharing the default data root) make each other fail with
    /// <see cref="IOException"/>. Sharing read/write/delete keeps logging best-effort instead of
    /// turning a shared log path into a startup failure.
    /// </remarks>
    private static void AppendShared(string logFilePath, string entry)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(entry);
        using var stream = new FileStream(
            logFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        stream.Write(bytes, 0, bytes.Length);
    }

    private string GetArchivePath(int archiveIndex)
    {
        string? directory = Path.GetDirectoryName(logFilePath);
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(logFilePath);
        string extension = Path.GetExtension(logFilePath);
        string archiveFileName = string.Concat(fileNameWithoutExtension, ".", archiveIndex.ToString(CultureInfo.InvariantCulture), extension);
        return string.IsNullOrWhiteSpace(directory)
            ? archiveFileName
            : Path.Join(directory, archiveFileName);
    }
}
