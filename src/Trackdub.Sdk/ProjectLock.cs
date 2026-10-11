using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Trackdub.Sdk;

/// <summary>
/// File-based project directory lock that prevents concurrent runs targeting the same project.
/// Uses an exclusive <see cref="FileStream"/> on a <c>.trackdub.lock</c> file to detect conflicts.
/// The sidecar persists between runs and its diagnostic metadata is overwritten after acquisition;
/// the open file handle, rather than the contents or age of the file, determines lock ownership.
/// Do not manually remove the sidecar while a run may hold its handle.
/// The holder also publishes its PID in a separate <c>.trackdub.lock.owner</c> record, because
/// the lock file's own exclusive handle refuses every other open (Windows sharing modes, and an
/// exclusive <c>flock</c> on Unix), so a contending process could never read the PID from it.
/// Lock identity follows symlinks (and case on case-insensitive file systems) so alternate paths
/// naming the same project still conflict.
/// </summary>
public sealed class ProjectLock : IDisposable, IAsyncDisposable
{
    private const string LockFileName = ".trackdub.lock";
    private const string OwnerFileName = ".trackdub.lock.owner";

    private static readonly object s_globalLock = new();

    /// <summary>
    /// In-process registry of currently held lock paths (canonical, and case-folded on
    /// case-insensitive file systems).  On Linux, <c>FileShare.None</c> is not
    /// enforced for intra-process opens, so we need this secondary guard to stop the
    /// same process from acquiring the same directory lock twice.
    /// </summary>
    private static readonly HashSet<string> s_heldPaths = new(StringComparer.Ordinal);

    private readonly string _registryKey;
    private readonly string _ownerFilePath;
    private FileStream? _lockStream;
    private volatile bool _disposed;

    private ProjectLock(string registryKey, string ownerFilePath, FileStream lockStream)
    {
        _registryKey = registryKey;
        _ownerFilePath = ownerFilePath;
        _lockStream = lockStream;
    }

    /// <summary>
    /// Acquires an exclusive lock on the specified project directory.
    /// </summary>
    /// <param name="projectDirectory">The project directory to lock.</param>
    /// <returns>A <see cref="ProjectLock"/> that must be disposed to release the lock.</returns>
    /// <exception cref="ProjectLockedException">
    /// Thrown when the project directory is already locked by another active process.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="projectDirectory"/> is null, whitespace, or invalid.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown when the project directory cannot be created or resolved, or lock information cannot be written.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Thrown when permissions, ownership, or a read-only filesystem prevents creating or accessing
    /// the project directory, lock sidecar, or case-sensitivity probe.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when the filesystem does not support a required directory or file operation.
    /// </exception>
    public static ProjectLock Acquire(string projectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        string fullPath = Path.GetFullPath(projectDirectory);
        string canonicalPath = CanonicalizeDirectoryPath(fullPath);
        string lockFilePath = Path.Join(canonicalPath, LockFileName);
        string ownerFilePath = Path.Join(canonicalPath, OwnerFileName);

        // Ensure the directory exists so we can create the lock file.
        Directory.CreateDirectory(canonicalPath);
        string registryKey = BuildRegistryKey(canonicalPath);

        // Serialize lock acquisition within this process to prevent races
        // between threads trying to lock the same directory.
        lock (s_globalLock)
        {
            // Intra-process guard: on Linux FileShare.None is not enforced between
            // FileStream instances within the same process, so we maintain our own set.
            if (s_heldPaths.Contains(registryKey))
            {
                throw new ProjectLockedException(projectDirectory, Environment.ProcessId);
            }

            // Attempt to open the lock file with exclusive access.
            FileStream? stream = TryOpenExclusive(lockFilePath);

            if (stream is null)
            {
                // The exclusive-open failure is authoritative: a file handle still owns the
                // lock even if its owner record is missing, corrupt, or stale. Retry once in
                // case the holder released the handle meanwhile. Never unlink the path: another
                // process could acquire a replacement between our stale check and delete,
                // causing both runs to hold different files under the same lock name.
                stream = TryOpenExclusive(lockFilePath);
                if (stream is null)
                {
                    // Read the record only after the retry failed: a holder deletes its record
                    // before releasing, so an earlier read could name a holder that has since
                    // been replaced. Read now, it names the current holder or nobody yet.
                    LockFileRead read = TryReadHoldingProcessId(ownerFilePath);
                    if (!read.ReadFailed && read.HoldingPid is { } holdingPid && IsProcessAlive(holdingPid))
                    {
                        throw new ProjectLockedException(projectDirectory, holdingPid);
                    }

                    throw new ProjectLockedException(projectDirectory);
                }
            }

            // Write diagnostic info (PID + timestamp) to the lock file.
            WriteLockInfo(stream);
            TryWriteOwnerRecord(ownerFilePath);

            s_heldPaths.Add(registryKey);
            return new ProjectLock(registryKey, ownerFilePath, stream);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseLock();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;

        ReleaseLock();
        return ValueTask.CompletedTask;
    }

    private void ReleaseLock()
    {
        FileStream? stream = Interlocked.Exchange(ref _lockStream, null);
        if (stream is null) return;

        // Delete the owner record while the handle still excludes every other holder, so this
        // can only remove our own record, never one a later holder has just published.
        try
        {
            File.Delete(_ownerFilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic only. A leftover record names a PID that no longer holds the lock, and
            // contenders report a PID only while the lock is held and that process is alive.
        }

        // Remove from the intra-process registry before releasing the file handle,
        // so that a racing Acquire on the same path sees the slot as free only after
        // the file lock is gone.
        lock (s_globalLock)
        {
            s_heldPaths.Remove(_registryKey);
        }

        try
        {
            // Dispose alone is sufficient (calls Close). Calling Close then Dispose risks
            // skipping Dispose if Close throws.
            stream.Dispose();
        }
        catch
        {
            // Best-effort close; the OS will release the handle on process exit regardless.
        }

        // Keep the sidecar file in place after releasing its handle. Deleting by path here
        // races with a new Acquire: it can acquire the same file between Dispose and Delete,
        // then have its live lock unlinked underneath it on Unix. The next Acquire safely
        // reuses the persistent file and overwrites the diagnostic metadata after locking it.
    }

    /// <summary>
    /// Resolves a path to its physical form, following symlinks in every component, so that two
    /// paths naming the same directory through different links produce the same identity for the
    /// in-process registry. On Windows the OS enforces <see cref="FileShare.None"/> between
    /// <see cref="FileStream"/> instances in one process, so the registry key only needs the
    /// lexical normalization that <see cref="Path.GetFullPath"/> already provides.
    /// </summary>
    private static string CanonicalizeDirectoryPath(string fullPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return fullPath;
        }

        string root = Path.GetPathRoot(fullPath) ?? Path.DirectorySeparatorChar.ToString();

        // Path.GetFullPath preserves a trailing separator. Left in place it makes the first
        // component below empty, the walk stops before collecting anything, and the whole
        // path collapses to the root — so the lock would be placed in the file system root
        // instead of the project directory. Trim it, keeping the root itself intact.
        string trimmed = fullPath;
        while (trimmed.Length > root.Length && trimmed[^1] == Path.DirectorySeparatorChar)
        {
            trimmed = trimmed[..^1];
        }

        // Collect the components beneath the root so each can be checked for being a symlink.
        var parts = new List<string>();
        string remaining = trimmed;
        while (remaining.Length > root.Length)
        {
            string name = Path.GetFileName(remaining);
            if (name.Length == 0)
            {
                break;
            }

            parts.Insert(0, name);
            remaining = Path.GetDirectoryName(remaining) ?? root;
        }

        string resolved = root;
        foreach (string part in parts)
        {
            resolved = Path.Join(resolved, part);
            if (Directory.Exists(resolved) &&
                new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // Resolve links in the target's ancestors as well. A link target may use
                // another alias for the same filesystem path (for example /var vs
                // /private/var on macOS), otherwise two spellings can still get different
                // registry keys after this component is followed.
                resolved = CanonicalizeDirectoryPath(target.FullName);
            }
        }

        return resolved;
    }

    /// <summary>
    /// Builds the in-process registry key for a canonical project path. Symlink resolution
    /// alone leaves case unnormalized, so on a case-insensitive file system — where the OS
    /// file lock is advisory and this registry is the only intra-process guard — two paths
    /// differing only in case would name one project under two keys. Case-fold there and
    /// leave case-sensitive volumes untouched, where <c>Foo</c> and <c>foo</c> really are
    /// two directories and folding would report a false conflict.
    /// </summary>
    private static string BuildRegistryKey(string canonicalPath) =>
        BuildRegistryKey(canonicalPath, FileSystemIsCaseInsensitive(canonicalPath));

    /// <summary>
    /// Key-building half of <see cref="BuildRegistryKey(string)"/>, taking the case-sensitivity
    /// answer from the caller so both volume kinds can be exercised on any host.
    /// </summary>
    internal static string BuildRegistryKey(string canonicalPath, bool caseInsensitiveFileSystem) =>
        caseInsensitiveFileSystem
            ? Path.Join(canonicalPath, LockFileName).ToUpperInvariant()
            : Path.Join(canonicalPath, LockFileName);

    /// <summary>
    /// Reports whether upper-casing a name still reaches the same entry, which is exactly the
    /// question the registry key depends on. The project directory is created before this probe,
    /// so the temporary entry stays inside the project instead of touching a shared ancestor.
    /// The probe creates one uniquely named entry there and asks the file system for that same
    /// entry under an upper-cased spelling: mere existence of the upper-cased spelling would also
    /// be answered by a case-variant *sibling* on a case-sensitive volume, which would report
    /// such a volume as case-insensitive and fold two distinct projects onto one registry key.
    /// A volume that cannot be probed reports false, which keeps the previous behaviour rather
    /// than guessing.
    /// </summary>
    internal static bool FileSystemIsCaseInsensitive(string directoryPath)
    {
        DirectoryInfo probe = new(directoryPath);
        if (!probe.Exists)
        {
            return false;
        }

        string probeName = $".trackdub-case-probe-{Guid.NewGuid():N}";
        string probeFile = Path.Join(probe.FullName, probeName);

        try
        {
            File.WriteAllText(probeFile, string.Empty);

            return File.Exists(Path.Join(probe.FullName, probeName.ToUpperInvariant()));
        }
        catch (IOException)
        {
            // Cannot create the probe here, so the question stays unanswered.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(probeFile);
            }
            catch (IOException)
            {
                // Best-effort cleanup; the unique probe cannot affect lock identity.
            }
            catch (UnauthorizedAccessException)
            {
                // Best-effort cleanup; the unique probe cannot affect lock identity.
            }
            catch (NotSupportedException)
            {
                // Best-effort cleanup for filesystems that do not support this operation.
            }
            catch (System.Security.SecurityException)
            {
                // Best-effort cleanup when filesystem security policy denies deletion.
            }
        }
    }

    /// <summary>
    /// Attempts to open the lock file with exclusive access (no sharing).
    /// Returns null if the file is already in use; permission failures propagate to the caller.
    /// </summary>
    private static FileStream? TryOpenExclusive(string lockFilePath)
    {
        try
        {
            return new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 256);
        }
        catch (IOException)
        {
            // File is locked by another process.
            return null;
        }
    }

    /// <summary>
    /// Attempts to read the holder's PID from its owner record (best-effort, non-exclusive read).
    /// Distinguishes "the file could not be read at all" from "the file is readable but carries
    /// no PID" so diagnostics can report a holder only when the data supports one. Acquisition
    /// itself is decided by the exclusive file handle, never by stale or missing metadata.
    /// </summary>
    private static LockFileRead TryReadHoldingProcessId(string ownerFilePath)
    {
        try
        {
            // The owner record is never held open, so the holder does not block this read.
            using var reader = new FileStream(
                ownerFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 256);

            using var sr = new StreamReader(reader);
            string content = sr.ReadToEnd();

            if (string.IsNullOrWhiteSpace(content))
            {
                // A record without a PID has an unknown holder; acquisition still follows
                // the file handle, not its diagnostic contents.
                return new LockFileRead(ReadFailed: false, HoldingPid: null);
            }

            // Parse the JSON lock info.
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("pid", out JsonElement pidElement) &&
                pidElement.TryGetInt32(out int pid))
            {
                return new LockFileRead(ReadFailed: false, HoldingPid: pid);
            }

            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch (JsonException)
        {
            // The file is readable but its contents are not lock info. Keep the holder unknown;
            // the exclusive handle retry below still decides whether the lock is available.
            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch (FileNotFoundException)
        {
            // No record: the holder has not published one yet, could not write it, or predates
            // owner records. The exclusive-open retry below still decides availability.
            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch (DirectoryNotFoundException)
        {
            // The project directory is gone, so there is no live holder either.
            return new LockFileRead(ReadFailed: false, HoldingPid: null);
        }
        catch
        {
            // The holder is unknown. Do not report a PID without readable diagnostic data;
            // acquisition still fails closed if the exclusive-open retry is denied.
            return new LockFileRead(ReadFailed: true, HoldingPid: null);
        }
    }

    /// <summary>
    /// Checks whether a process with the given PID is still running.
    /// </summary>
    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process does not exist.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Process has exited.
            return false;
        }
    }

    /// <summary>
    /// Writes diagnostic information (PID and timestamp) to the lock file.
    /// </summary>
    private static void WriteLockInfo(FileStream stream)
    {
        stream.SetLength(0);
        stream.Position = 0;

        JsonSerializer.Serialize(stream, CreateLockInfo());
        stream.Flush();
    }

    /// <summary>
    /// Publishes this process's owner record next to the lock file, where a contending process
    /// can read it. The record is written to a unique temporary file and moved into place, so a
    /// reader never sees a partial record. Best-effort: a missing record only costs the PID in a
    /// contender's message, so failing to write it must not fail the run that holds the lock.
    /// </summary>
    private static void TryWriteOwnerRecord(string ownerFilePath)
    {
        string tempPath = $"{ownerFilePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(CreateLockInfo()));
            File.Move(tempPath, ownerFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostic only: a missing record costs the PID in a contender's message, never the run.
            try
            {
                File.Delete(tempPath);
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Nothing more to do; the temporary file carries no lock state.
            }
        }
    }

    private static LockFileContent CreateLockInfo() => new()
    {
        pid = Environment.ProcessId,
        timestamp = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        machineName = Environment.MachineName
    };

    /// <summary>
    /// Result of reading the owner record's holder PID. <see cref="ReadFailed"/> is set when the file
    /// could not be read at all (the holder is unknown); a readable file without a PID — such as
    /// a record with no <c>pid</c> field — reports <c>ReadFailed = false</c> with a
    /// null PID. In either case, the exclusive file handle, not its contents, determines whether
    /// the lock is available.
    /// </summary>
    private readonly record struct LockFileRead(bool ReadFailed, int? HoldingPid);

    /// <summary>
    /// JSON structure written to the lock file and the owner record for diagnostics.
    /// </summary>
    private sealed record LockFileContent
    {
        public int pid { get; init; }
        public string timestamp { get; init; } = "";
        public string machineName { get; init; } = "";
    }
}
