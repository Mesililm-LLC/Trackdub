using Trackdub.Sdk;
using Trackdub.Sdk.Composition;
using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Property-based tests verifying session isolation: creating N sessions from one factory
/// and mutating each independently never causes cross-session state leakage.
///
/// **Validates: Requirements 14.1, 14.2**
/// </summary>
public sealed class TrackdubSessionIsolationPropertyTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    /// <summary>
    /// Property 2: Session isolation — each session created from the same factory
    /// has its own independent ProjectRootPath and workspace instance.
    /// No cross-session state leakage occurs when sessions are created with different paths.
    ///
    /// **Validates: Requirements 14.1, 14.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool Sessions_HaveIndependent_ProjectRootPaths(PositiveInt sessionCount)
    {
        int count = Math.Clamp(sessionCount.Get, 2, 8);

        using var factory = CreateFactory();
        var sessions = new List<TrackdubSession>();
        var tempPaths = new List<string>();

        try
        {
            // Create N sessions with unique temp directories
            for (int i = 0; i < count; i++)
            {
                string tempDir = CreateTempProjectDir();
                tempPaths.Add(tempDir);
                sessions.Add(factory.CreateSession(tempDir));
            }

            // Verify each session has its own distinct ProjectRootPath
            for (int i = 0; i < sessions.Count; i++)
            {
                string expectedPath = Path.GetFullPath(tempPaths[i]);
                if (!string.Equals(sessions[i].ProjectRootPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Verify no two sessions share the same ProjectRootPath
            var distinctPaths = sessions.Select(s => s.ProjectRootPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinctPaths.Count != sessions.Count)
                return false;

            return true;
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    /// <summary>
    /// Property 2 (continued): Each session's Workspace is a distinct instance —
    /// no shared mutable state between sessions created from the same factory.
    ///
    /// **Validates: Requirements 14.1, 14.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool Sessions_HaveDistinct_WorkspaceInstances(PositiveInt sessionCount)
    {
        int count = Math.Clamp(sessionCount.Get, 2, 8);

        using var factory = CreateFactory();
        var sessions = new List<TrackdubSession>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                string tempDir = CreateTempProjectDir();
                sessions.Add(factory.CreateSession(tempDir));
            }

            // Verify each session has a distinct Workspace reference
            for (int i = 0; i < sessions.Count; i++)
            {
                for (int j = i + 1; j < sessions.Count; j++)
                {
                    if (ReferenceEquals(sessions[i].Workspace, sessions[j].Workspace))
                        return false;
                }
            }

            return true;
        }
        finally
        {
            foreach (var session in sessions)
                session.Dispose();
        }
    }

    /// <summary>
    /// Property 2 (continued): Disposing one session does not affect another session
    /// created from the same factory. The surviving session's ProjectRootPath and
    /// Workspace remain accessible.
    ///
    /// **Validates: Requirements 14.1, 14.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool DisposingOneSession_DoesNotAffect_OtherSessions(PositiveInt sessionCount)
    {
        int count = Math.Clamp(sessionCount.Get, 2, 6);

        using var factory = CreateFactory();
        var sessions = new List<TrackdubSession>();
        var disposedIndices = new HashSet<int>();

        try
        {
            for (int i = 0; i < count; i++)
            {
                string tempDir = CreateTempProjectDir();
                sessions.Add(factory.CreateSession(tempDir));
            }

            // Dispose every other session
            for (int i = 0; i < sessions.Count; i += 2)
            {
                sessions[i].Dispose();
                disposedIndices.Add(i);
            }

            // Verify surviving sessions are still functional
            for (int i = 0; i < sessions.Count; i++)
            {
                if (disposedIndices.Contains(i))
                    continue;

                // Accessing ProjectRootPath on a surviving session should not throw
                string path = sessions[i].ProjectRootPath;
                if (string.IsNullOrWhiteSpace(path))
                    return false;

                // Workspace should still be accessible
                if (sessions[i].Workspace is null)
                    return false;
            }

            return true;
        }
        finally
        {
            // Dispose remaining sessions
            for (int i = 0; i < sessions.Count; i++)
            {
                if (!disposedIndices.Contains(i))
                    sessions[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically,
    /// ensuring test discovery works with xunit.runner.visualstudio v3.
    /// Tests the same session isolation property.
    ///
    /// **Validates: Requirements 14.1, 14.2**
    /// </summary>
    [Fact]
    public void SessionIsolation_PropertyCheck_ViaFact()
    {
        // One factory for every case: the property is about sessions created from the same
        // factory, and rebuilding the headless composition per case dominated the runtime.
        using var factory = CreateFactory();
        Prop.ForAll<PositiveInt>(sessionCount =>
        {
            int count = Math.Clamp(sessionCount.Get, 2, 6);
            var sessions = new List<TrackdubSession>();

            try
            {
                for (int i = 0; i < count; i++)
                {
                    string tempDir = CreateTempProjectDir();
                    sessions.Add(factory.CreateSession(tempDir));
                }

                // Each session has independent ProjectRootPath
                var distinctPaths = sessions
                    .Select(s => s.ProjectRootPath)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                // Each session has distinct Workspace instance
                bool allDistinctWorkspaces = true;
                for (int i = 0; i < sessions.Count && allDistinctWorkspaces; i++)
                    for (int j = i + 1; j < sessions.Count && allDistinctWorkspaces; j++)
                        allDistinctWorkspaces = !ReferenceEquals(sessions[i].Workspace, sessions[j].Workspace);

                // Disposing one doesn't affect others
                if (sessions.Count >= 2)
                {
                    sessions[0].Dispose();
                    string survivorPath = sessions[1].ProjectRootPath;
                    bool survivorOk = !string.IsNullOrWhiteSpace(survivorPath) && sessions[1].Workspace is not null;

                    return (distinctPaths == sessions.Count)
                        .And(allDistinctWorkspaces)
                        .And(survivorOk);
                }

                return (distinctPaths == sessions.Count).And(allDistinctWorkspaces);
            }
            finally
            {
                foreach (var session in sessions)
                {
                    try { session.Dispose(); } catch { /* already disposed */ }
                }
            }
        }).QuickCheckThrowOnFailure();
    }

    private TrackdubSessionFactory CreateFactory()
    {
        var options = new TrackdubOptions();
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(options);
        var provider = services.BuildServiceProvider();
        return new TrackdubSessionFactory(provider);
    }

    private string CreateTempProjectDir()
    {
        string dir = Path.Join(Path.GetTempPath(), "TrackdubTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        // Clean up parent directory if empty
        string parentDir = Path.Join(Path.GetTempPath(), "TrackdubTests");
        try
        {
            if (Directory.Exists(parentDir) && !Directory.EnumerateFileSystemEntries(parentDir).Any())
                Directory.Delete(parentDir);
        }
        catch { /* best-effort cleanup */ }
    }
}
