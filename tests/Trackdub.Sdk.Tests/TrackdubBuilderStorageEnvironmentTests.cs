using Microsoft.Extensions.DependencyInjection;

using Trackdub.Infrastructure.Settings;
using Trackdub.Sdk.Composition;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Verifies that <see cref="TrackdubBuilder.WithModelDirectory"/> actually reaches static
/// consumers that read <c>TRACKDUB_*_ROOT</c> environment variables directly (rather than
/// through DI), and that the override is undone when the session factory is disposed.
/// Regression coverage for a bug where the override was registered as a lazy DI singleton
/// that nothing ever resolved, making it a silent no-op.
/// </summary>
/// <remarks>
/// Runs in the non-parallel <c>StorageEnvCollection</c> because the tests mutate
/// process-global <c>TRACKDUB_*</c> environment variables; parallel execution with other
/// tests reading/writing the same process env would cause flaky, order-dependent results.
/// The constructor primes a stable baseline by running a throwaway <c>AddTrackdub()</c>
/// composition first: that registration unconditionally applies its own default storage
/// paths to the process environment as an unrelated side effect, so we capture that post-
/// priming state once. Every later assertion compares against this primed baseline rather
/// than whatever was set (or unset) before any Trackdub composition ran in the process.
/// </remarks>
[Collection("StorageEnvCollection")]
public sealed class TrackdubBuilderStorageEnvironmentTests : IDisposable
{
    private static readonly string[] ManagedEnvironmentVariables =
    [
        TrackdubStoragePathResolver.DataRootEnvironmentVariable,
        TrackdubStoragePathResolver.CacheRootEnvironmentVariable,
        TrackdubStoragePathResolver.ToolCacheRootEnvironmentVariable,
        TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable,
        TrackdubStoragePathResolver.SharedAssetRootEnvironmentVariable,
        TrackdubStoragePathResolver.PortableEnvironmentVariable,
    ];

    private readonly string _modelDirectory;
    private readonly IReadOnlyDictionary<string, string?> _baselineEnvironment;
    private readonly string? _baselineDataRoot;
    private readonly string? _baselineCacheRoot;

    public TrackdubBuilderStorageEnvironmentTests()
    {
        _modelDirectory = Directory.CreateTempSubdirectory("trackdub-builder-env-test-").FullName;

        // AddTrackdub() unconditionally applies its own default storage paths to the
        // process env as an unrelated side effect (unchanged by this fix). Prime that
        // once so the "baseline" we compare against below is the state a headless
        // override's Dispose() actually restores to, not whatever was set (or unset)
        // before any Trackdub composition ran in this process.
        using (new TrackdubBuilder().Build())
        {
        }

        _baselineEnvironment = ManagedEnvironmentVariables.ToDictionary(
            variable => variable,
            Environment.GetEnvironmentVariable,
            StringComparer.OrdinalIgnoreCase);
        _baselineDataRoot = _baselineEnvironment[TrackdubStoragePathResolver.DataRootEnvironmentVariable];
        _baselineCacheRoot = _baselineEnvironment[TrackdubStoragePathResolver.CacheRootEnvironmentVariable];
    }

    [Fact]
    public void Build_WithModelDirectory_AppliesOverrideToProcessEnvironment()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithModelDirectory(_modelDirectory)
            .Build();

        Assert.Equal(
            _modelDirectory,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
    }

    [Fact]
    public void Build_WithModelDirectory_PreservesExplicitEngineCacheRoot()
    {
        string? previousEngineCacheRoot = Environment.GetEnvironmentVariable(
            TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable);
        string explicitEngineCacheRoot = Path.Join(_modelDirectory, "shared-engine-cache");
        Environment.SetEnvironmentVariable(
            TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable,
            explicitEngineCacheRoot);

        try
        {
            using TrackdubSessionFactory factory = new TrackdubBuilder()
                .WithModelDirectory(_modelDirectory)
                .Build();

            Assert.Equal(
                explicitEngineCacheRoot,
                Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable,
                previousEngineCacheRoot);
        }
    }

    [Fact]
    public void DirectCompositionFactory_AppliesOverrideBeforeSessionWork()
    {
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub(new TrackdubOptions
        {
            ModelDirectory = _modelDirectory,
            ModelCacheDirectory = _modelDirectory,
        });

        using var factory = new TrackdubSessionFactory(services.BuildServiceProvider());

        Assert.Equal(
            _modelDirectory,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));

        factory.Dispose();

        Assert.Equal(
            _baselineCacheRoot,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
    }

    [Fact]
    public void Dispose_RestoresPriorProcessEnvironment()
    {
        TrackdubSessionFactory factory = new TrackdubBuilder()
            .WithModelDirectory(_modelDirectory)
            .Build();

        factory.Dispose();

        Assert.Equal(
            _baselineDataRoot,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.DataRootEnvironmentVariable));
        Assert.Equal(
            _baselineCacheRoot,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
    }

    /// <summary>
    /// Normally nested scopes restore the preceding scope and then the true baseline as they
    /// unwind in last-in-first-out order.
    /// </summary>
    [Fact]
    public void Dispose_NormalOrder_RebasesAllTheWayToTrueBaseline()
    {
        string secondModelDirectory = Directory.CreateTempSubdirectory("trackdub-builder-env-test-").FullName;
        try
        {
            using (TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
                       CreateStoragePaths(secondModelDirectory)))
            {
                using (TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
                           CreateStoragePaths(_modelDirectory)))
                {
                    // While both scopes are active, the most recently applied value wins.
                    Assert.Equal(
                        _modelDirectory,
                        Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
                }

                // The later scope restores the earlier scope's value.
                Assert.Equal(
                    secondModelDirectory,
                    Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
            }

            // Once the earlier scope also disposes, env must rebase all the way back
            // to the true original baseline — not stuck on an intermediate value.
            Assert.Equal(
                _baselineDataRoot,
                Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.DataRootEnvironmentVariable));
            Assert.Equal(
                _baselineCacheRoot,
                Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
        }
        finally
        {
            DeleteDirectoryBestEffort(secondModelDirectory);
        }
    }

    [Fact]
    public void Dispose_OutOfOrder_PreservesNewerScopeThenRestoresTrueBaseline()
    {
        string secondModelDirectory = Directory.CreateTempSubdirectory("trackdub-builder-env-test-").FullName;
        using IDisposable older = TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
            CreateStoragePaths(_modelDirectory));
        using IDisposable newer = TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
            CreateStoragePaths(secondModelDirectory));

        try
        {
            older.Dispose();

            Assert.Equal(
                secondModelDirectory,
                Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));

            newer.Dispose();

            Assert.Equal(
                _baselineCacheRoot,
                Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
        }
        finally
        {
            DeleteDirectoryBestEffort(secondModelDirectory);
        }
    }

    [Fact]
    public void Dispose_Twice_IsIdempotent()
    {
        using IDisposable scope = TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
            CreateStoragePaths(_modelDirectory));

        scope.Dispose();
        scope.Dispose();

        Assert.Equal(
            _baselineCacheRoot,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
    }

    [Fact]
    public void Dispose_AfterUnscopedApply_PreservesNewerProcessValues()
    {
        string unscopedRoot = Path.Join(
            Path.GetTempPath(),
            "trackdub-unscoped-cache-" + Guid.NewGuid().ToString("N"));
        using IDisposable scope = TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
            CreateStoragePaths(_modelDirectory));

        TrackdubStoragePathResolver.ApplyToCurrentProcess(CreateStoragePaths(unscopedRoot));
        string? unscopedCacheRoot = Environment.GetEnvironmentVariable(
            TrackdubStoragePathResolver.CacheRootEnvironmentVariable);

        scope.Dispose();

        Assert.Equal(
            unscopedCacheRoot,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
    }

    [Fact]
    public void Dispose_AfterDirectExternalWrite_PreservesNewerProcessValue()
    {
        string externalCacheRoot = Path.Join(
            Path.GetTempPath(),
            "trackdub-external-cache-" + Guid.NewGuid().ToString("N"));
        using IDisposable scope = TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(
            CreateStoragePaths(_modelDirectory));

        Environment.SetEnvironmentVariable(
            TrackdubStoragePathResolver.CacheRootEnvironmentVariable,
            externalCacheRoot);

        scope.Dispose();

        Assert.Equal(
            externalCacheRoot,
            Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
    }

    /// <summary>
    /// After every scoped override for a variable has been disposed, the backing stack entry
    /// must be dropped entirely so a later host re-captures whatever the process environment
    /// now holds as a fresh true baseline. If <c>TRACKDUB_*</c> is changed between headless
    /// hosts (e.g. another composition or a test setting a different cache root), disposing
    /// the later scope must restore that current value — not a stale first-seen baseline that
    /// would corrupt static consumers. Regression coverage for the stale-baseline bug.
    /// </summary>
    [Fact]
    public void Dispose_ThenReapply_AfterEnvChange_CapturesTrueBaseline()
    {
        string secondModelDirectory = Directory.CreateTempSubdirectory("trackdub-builder-env-test-").FullName;
        try
        {
            // First host applies and fully disposes, dropping the stack entry.
            using (TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(CreateStoragePaths(_modelDirectory)))
            {
            }

            // The process environment has since moved on to a different value.
            string changedCacheRoot =
                Path.Join(Path.GetTempPath(), "trackdub-changed-cache-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable, changedCacheRoot);

            // A fresh host captures the *current* value as its true baseline, then applies
            // its own override on top.
            string? secondAppliedCacheRoot;
            using (TrackdubStoragePathResolver.ApplyToCurrentProcessScoped(CreateStoragePaths(secondModelDirectory)))
            {
                secondAppliedCacheRoot = Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable);

                // While active the override is applied (and differs from the changed baseline).
                Assert.NotEqual(changedCacheRoot, secondAppliedCacheRoot);
                Assert.Equal(
                    secondAppliedCacheRoot,
                    Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
            }

            // Disposing the fresh host restores the current process value (changedCacheRoot),
            // not the stale first-seen baseline.
            Assert.NotEqual(_baselineCacheRoot, changedCacheRoot);
            Assert.Equal(
                changedCacheRoot,
                Environment.GetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TrackdubStoragePathResolver.CacheRootEnvironmentVariable, _baselineCacheRoot);
            DeleteDirectoryBestEffort(secondModelDirectory);
        }
    }

    public void Dispose()
    {
        foreach ((string variable, string? value) in _baselineEnvironment)
        {
            Environment.SetEnvironmentVariable(variable, value);
        }

        DeleteDirectoryBestEffort(_modelDirectory);
    }

    private static void DeleteDirectoryBestEffort(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private static TrackdubStoragePaths CreateStoragePaths(string root) =>
        new(new TrackdubStorageOptions(root, root, SharedAssetRoot: null, IsPortable: false));
}
