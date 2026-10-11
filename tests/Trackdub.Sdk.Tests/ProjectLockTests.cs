using System.Text.Json;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Unit tests for <see cref="ProjectLock"/> file-based locking mechanism.
///
/// **Validates: Requirements 14.3**
/// </summary>
public sealed class ProjectLockTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void Acquire_CreatesLockFile_InProjectDirectory()
    {
        // Arrange
        string dir = CreateTempDirectory();

        // Act
        using var lockHandle = ProjectLock.Acquire(dir);

        // Assert — lock file exists (DeleteOnClose means it exists while stream is open)
        string lockPath = Path.Join(dir, ".trackdub.lock");
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public void Acquire_SameDirectory_ThrowsProjectLockedException()
    {
        // Arrange
        string dir = CreateTempDirectory();
        using var firstLock = ProjectLock.Acquire(dir);

        // Act & Assert
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Contains(dir, ex.ProjectDirectory);
    }

    [Fact]
    public void Acquire_DifferentDirectories_BothSucceed()
    {
        // Arrange
        string dir1 = CreateTempDirectory();
        string dir2 = CreateTempDirectory();

        // Act
        using var lock1 = ProjectLock.Acquire(dir1);
        using var lock2 = ProjectLock.Acquire(dir2);

        // Assert — both acquired without exception
        Assert.NotNull(lock1);
        Assert.NotNull(lock2);
    }

    [Fact]
    public void Dispose_ReleasesLock_AllowsReacquisition()
    {
        // Arrange
        string dir = CreateTempDirectory();
        var firstLock = ProjectLock.Acquire(dir);

        // Act — release the lock
        firstLock.Dispose();

        // Assert — can acquire again
        using var secondLock = ProjectLock.Acquire(dir);
        Assert.NotNull(secondLock);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesLock_AllowsReacquisition()
    {
        // Arrange
        string dir = CreateTempDirectory();
        var firstLock = ProjectLock.Acquire(dir);

        // Act — release the lock asynchronously
        await firstLock.DisposeAsync();

        // Assert — can acquire again
        using var secondLock = ProjectLock.Acquire(dir);
        Assert.NotNull(secondLock);
    }

    [Fact]
    public void Dispose_MultipleTimes_DoesNotThrow()
    {
        // Arrange
        string dir = CreateTempDirectory();
        var lockHandle = ProjectLock.Acquire(dir);

        // Act & Assert — idempotent disposal
        lockHandle.Dispose();
        var exception = Record.Exception(() => lockHandle.Dispose());
        Assert.Null(exception);
    }

    [Fact]
    public void Acquire_NullDirectory_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => ProjectLock.Acquire(null!));
    }

    [Fact]
    public void Acquire_WhitespaceDirectory_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => ProjectLock.Acquire("   "));
    }

    [Fact]
    public void Acquire_CreatesDirectoryIfNotExists()
    {
        // Arrange
        string parentDir = CreateTempDirectory();
        string subDir = Path.Join(parentDir, "nested", "project");
        Assert.False(Directory.Exists(subDir));

        // Act
        using var lockHandle = ProjectLock.Acquire(subDir);

        // Assert
        Assert.True(Directory.Exists(subDir));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    public void Acquire_UnheldLockFileFromPreviousRun_ReusesAndOverwritesDiagnostics(string priorContents)
    {
        // Arrange — a previous run left a sidecar without a usable holder PID.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, priorContents);

        // Act — no process owns the file handle, so the OS lock is available regardless
        // of what diagnostic data remains on disk.
        using (ProjectLock.Acquire(dir))
        {
            Assert.True(File.Exists(lockPath));
        }

        Assert.Contains($"\"pid\":{Environment.ProcessId}", File.ReadAllText(lockPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Acquire_ConcurrentThreads_OnlyOneSucceeds()
    {
        // Arrange
        string dir = CreateTempDirectory();
        int successCount = 0;
        int failureCount = 0;
        var barrier = new Barrier(4);

        // Act — race 4 threads to acquire the same lock
        var threads = Enumerable.Range(0, 4).Select(_ => new Thread(() =>
        {
            barrier.SignalAndWait();
            try
            {
                using var lockHandle = ProjectLock.Acquire(dir);
                Interlocked.Increment(ref successCount);
                // Hold the lock briefly
                Thread.Sleep(50);
            }
            catch (ProjectLockedException)
            {
                Interlocked.Increment(ref failureCount);
            }
        })).ToList();

        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        // Assert — exactly one thread should have acquired the lock
        Assert.Equal(1, successCount);
        Assert.Equal(3, failureCount);
    }

    [Fact]
    public void ProjectLockedException_HasCorrectErrorCode()
    {
        var ex = new ProjectLockedException("/some/path");
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal("/some/path", ex.ProjectDirectory);
        Assert.Null(ex.HoldingProcessId);
    }

    [Fact]
    public void ProjectLockedException_WithPid_IncludesPidInMessage()
    {
        var ex = new ProjectLockedException("/some/path", 12345);
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal("/some/path", ex.ProjectDirectory);
        Assert.Equal(12345, ex.HoldingProcessId);
        Assert.Contains("12345", ex.Message);
    }

    [Fact]
    public void Acquire_LockHeldExclusivelyByAnotherProcess_ReportsTheHolderPid()
    {
        // Arrange — reproduce what a run in another process leaves on disk: the lock file open
        // with FileShare.None, exactly as Acquire opens it, and that run's owner record. No
        // registry entry exists for this directory, so a PID can only come from the record.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        using FileStream holder = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        File.WriteAllText(
            Path.Join(dir, ".trackdub.lock.owner"),
            $$"""{"pid":{{Environment.ProcessId}},"timestamp":"2026-10-11T00:00:00Z","machineName":"HOLDER"}""");

        // The premises: a real holder refuses the exclusive open and the shared read of the
        // lock file alike, so the lock file itself can never carry the PID to a contender.
        Assert.False(CanOpenExclusively(lockPath));
        Assert.False(CanReadShared(lockPath));

        // Act
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));

        // Assert
        Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
        Assert.True(File.Exists(lockPath));
    }

    [Fact]
    public void Acquire_WritesOwnerRecordWhileHeld_AndRemovesItOnRelease()
    {
        // Arrange
        string dir = CreateTempDirectory();
        string ownerPath = Path.Join(dir, ".trackdub.lock.owner");

        // Act & Assert — the record names this process while the lock is held...
        using (ProjectLock.Acquire(dir))
        {
            using JsonDocument record = JsonDocument.Parse(File.ReadAllText(ownerPath));
            Assert.Equal(Environment.ProcessId, record.RootElement.GetProperty("pid").GetInt32());
        }

        // ...and is gone once it is released, so a later holder's contender never reads it.
        Assert.False(File.Exists(ownerPath));
        Assert.Empty(Directory.EnumerateFiles(dir, ".trackdub.lock.owner*"));
    }

    [UnixOnlyFact]
    public void Acquire_DirectoryWithTrailingSeparator_LocksTheProjectDirectory()
    {
        // Arrange — a caller-supplied output directory can arrive with a trailing separator.
        string dir = CreateTempDirectory();

        // Act
        using var lockHandle = ProjectLock.Acquire(dir + Path.DirectorySeparatorChar);

        // Assert — the lock file lands in the project directory, not in the file system root.
        Assert.True(File.Exists(Path.Join(dir, ".trackdub.lock")));
    }

    [UnixOnlyFact]
    public void Acquire_EmptyLockFileHeldByAnotherHandle_FailsClosed()
    {
        // Arrange — another handle owns an empty lock file whose timestamp looks stale.
        // The active OS lock, not its diagnostic contents or age, determines ownership.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, string.Empty);

        // The lock file keeps its default mode, so the exclusive probe below is refused by the
        // holder's share mode alone and not by file permissions, which would make the premise
        // hold for the wrong reason. The holder shares the file so the production read for its
        // diagnostic still works: on Unix the holder takes a shared lock for any FileShare other
        // than None.
        using FileStream holder = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // The premises of the test: the exclusive open must be refused and the shared read
        // must succeed. If either fails the branch under test is skipped and this test would
        // pass against any code.
        Assert.False(CanOpenExclusively(lockPath));
        Assert.True(CanReadShared(lockPath));

        // Act — an open handle is authoritative even if its contents still look stale.
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));

        // Assert — do not unlink and replace a lock file that another handle still owns.
        Assert.Null(ex.HoldingProcessId);
        Assert.True(File.Exists(lockPath));
    }

    [UnixOnlyFact]
    public void Acquire_CorruptLockFileHeldByAnotherHandle_FailsClosed()
    {
        // Arrange — another handle owns a lock file with malformed diagnostic contents.
        // A parse failure must not authorize unlinking a file that is still actively held.
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, """{"pid":12345,"machineN""");

        // The lock file keeps its default mode, so the exclusive probe below is refused by the
        // holder's share mode alone and not by file permissions, which would make the premise
        // hold for the wrong reason. The holder shares the file so the production read for its
        // diagnostic still works: on Unix the holder takes a shared lock for any FileShare other
        // than None.
        using FileStream holder = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // The premises of the test: the exclusive open must be refused and the shared read
        // must succeed. If either fails the branch under test is skipped and this test would
        // pass against any code.
        Assert.False(CanOpenExclusively(lockPath));
        Assert.True(CanReadShared(lockPath));

        // Act — an open handle is authoritative even if its contents still look stale.
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(dir));

        // Assert — do not unlink and replace a lock file that another handle still owns.
        Assert.Null(ex.HoldingProcessId);
        Assert.True(File.Exists(lockPath));
    }

    [UnixOnlyFact]
    public void Acquire_UnreadableLockFile_ReportsPermissionFailureWithoutDeleting()
    {
        // Arrange — a lock file whose content cannot be read (mode 000).
        string dir = CreateTempDirectory();
        string lockPath = Path.Join(dir, ".trackdub.lock");
        File.WriteAllText(lockPath, """{"pid":2147483647,"timestamp":"2024-01-01T00:00:00Z","machineName":"STALE"}""");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(lockPath, UnixFileMode.None);
        }

        // If the environment can still read the file (for example running as root), the
        // premise of the test — an unreadable lock file — does not hold, so there is
        // nothing to assert.
        try
        {
            using var probe = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (probe.CanRead)
            {
                return;
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Expected — the file is unreadable; proceed with the assertion.
        }
        catch (IOException)
        {
            // Expected — the file is unreadable; proceed with the assertion.
        }

        // Act — permissions prevent acquiring the sidecar. Report the permission failure,
        // not a fictitious active holder, and leave the existing file untouched.
        Assert.Throws<UnauthorizedAccessException>(() => ProjectLock.Acquire(dir));

        // Assert
        Assert.True(File.Exists(lockPath));
    }

    [UnixOnlyFact]
    public void Acquire_SamePhysicalDirectory_ViaSymlink_ThrowsProjectLockedException()
    {
        // Arrange — a symlink naming the same directory as the real path.
        string parent = CreateTempDirectory();
        string realProject = Path.Join(parent, "real");
        Directory.CreateDirectory(realProject);
        string link = Path.Join(parent, "alias");
        Directory.CreateSymbolicLink(link, realProject);

        using var firstLock = ProjectLock.Acquire(realProject);

        // Act & Assert — the symlink path resolves to the same lock identity, so the
        // in-process registry reports a conflict rather than allowing a second run.
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(link));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
    }

    [UnixOnlyFact]
    public void Acquire_SamePhysicalDirectory_ViaSymlinkedAncestor_ThrowsProjectLockedException()
    {
        // Arrange — a symlinked ancestor: "/parent/alias/proj" names the same directory
        // as "/parent/real/proj".
        string parent = CreateTempDirectory();
        string realRoot = Path.Join(parent, "real");
        string realProject = Path.Join(realRoot, "proj");
        Directory.CreateDirectory(realProject);
        string linkRoot = Path.Join(parent, "alias");
        Directory.CreateSymbolicLink(linkRoot, realRoot);

        using var firstLock = ProjectLock.Acquire(realProject);

        // Act & Assert
        var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(Path.Join(linkRoot, "proj")));
        Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
        Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
    }

    [UnixOnlyFact]
    public void Dispose_ReleaseViaRealPath_AllowsAcquireViaSymlink()
    {
        // Arrange — a symlink naming the same directory as the real path.
        string parent = CreateTempDirectory();
        string realProject = Path.Join(parent, "real");
        Directory.CreateDirectory(realProject);
        string link = Path.Join(parent, "alias");
        Directory.CreateSymbolicLink(link, realProject);

        // Act — release the lock acquired through the real path...
        var firstLock = ProjectLock.Acquire(realProject);
        firstLock.Dispose();

        // Assert — ...then re-acquire through the symlink succeeds because both resolve
        // to the same registry key.
        using var secondLock = ProjectLock.Acquire(link);
        Assert.NotNull(secondLock);
    }

    [Fact]
    public void BuildRegistryKey_FoldsCaseOnlyOnCaseInsensitiveVolumes()
    {
        // Arrange — one project named two ways, without relying on how the host volume spells
        // case: the leaf carries letters, so the upper-cased spelling always differs.
        string dir = CreateTempDirectory();
        string flippedDir = Path.Join(Path.GetDirectoryName(dir)!, Path.GetFileName(dir).ToUpperInvariant());

        // Act & Assert — on a case-insensitive volume both spellings name one project and must
        // share a key; on a case-sensitive volume they name two projects and must not.
        Assert.Equal(
            ProjectLock.BuildRegistryKey(dir, caseInsensitiveFileSystem: true),
            ProjectLock.BuildRegistryKey(flippedDir, caseInsensitiveFileSystem: true));
        Assert.NotEqual(
            ProjectLock.BuildRegistryKey(dir, caseInsensitiveFileSystem: false),
            ProjectLock.BuildRegistryKey(flippedDir, caseInsensitiveFileSystem: false));
    }

    [Fact]
    public void Acquire_SameDirectory_WithDifferentCase_SharesOneRegistryKey()
    {
        // Arrange — a GUID name contains hex letters, so flipping the case of the leaf
        // either names the same directory (case-insensitive file system) or a different
        // one (case-sensitive file system). The registry key must follow suit.
        string dir = CreateTempDirectory();
        string flippedDir = Path.Join(Path.GetDirectoryName(dir)!, Path.GetFileName(dir).ToUpperInvariant());
        bool sameDirectory = flippedDir == dir || Directory.Exists(flippedDir);

        if (!sameDirectory)
        {
            _tempDirs.Add(flippedDir);
        }

        using var firstLock = ProjectLock.Acquire(dir);

        if (sameDirectory)
        {
            // Act & Assert — one project under two spellings, so the registry reports the
            // conflict instead of letting a second run share it.
            var ex = Assert.Throws<ProjectLockedException>(() => ProjectLock.Acquire(flippedDir));
            Assert.Equal(ErrorCode.ProjectLocked, ex.ErrorCode);
            Assert.Equal(Environment.ProcessId, ex.HoldingProcessId);
        }
        else
        {
            // Act & Assert — two distinct directories, so folding case would report a
            // false conflict for a project nobody holds.
            using var secondLock = ProjectLock.Acquire(flippedDir);
            Assert.NotNull(secondLock);
        }
    }

    [UnixOnlyFact]
    public void FileSystemIsCaseInsensitive_CaseVariantSibling_ReportsCaseSensitive()
    {
        // Arrange — a case-sensitive volume holding two directories that differ only in case.
        // The upper-cased spelling exists, but it is a different directory.
        string parent = CreateTempDirectory();
        string lower = Path.Join(parent, "project");
        string upper = Path.Join(parent, "PROJECT");
        Directory.CreateDirectory(lower);
        if (Directory.Exists(upper))
        {
            // Case-insensitive volume: both spellings are one directory, so there is
            // nothing to prove here.
            return;
        }

        Directory.CreateDirectory(upper);
        _tempDirs.Add(upper);

        // Act & Assert — the sibling must not be mistaken for a case-insensitive volume,
        // because folding case would report a conflict between two real projects.
        Assert.False(ProjectLock.FileSystemIsCaseInsensitive(lower));
        Assert.Empty(Directory.EnumerateFileSystemEntries(lower));
    }

    /// <summary>
    /// Reports whether the lock file can be opened the way <see cref="ProjectLock"/> opens
    /// it. Used to prove that a test's premise — the exclusive open fails — actually holds
    /// on this platform, so the branch under test is reached.
    /// </summary>
    private static bool CanOpenExclusively(string lockPath)
    {
        try
        {
            using FileStream probe = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reports whether the lock file's contents can be read the way <see cref="ProjectLock"/>
    /// reads them. Used to prove a test's premise — the holder does not block the diagnostic
    /// read — so the branch under test is reached.
    /// </summary>
    private static bool CanReadShared(string lockPath)
    {
        try
        {
            using FileStream probe = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _ = probe.ReadByte();
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private string CreateTempDirectory()
    {
        string dir = Path.Join(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}

/// <summary>
/// Marks a test whose reproduction depends on Unix file, permission and symlink semantics.
/// Reports as skipped on Windows instead of returning early and reporting as passed.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = "Unix-only test: the reproduction depends on Unix file, permission and symlink semantics.";
        }
    }
}
