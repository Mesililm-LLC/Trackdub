using System.Reflection;
using Trackdub.Application.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Tests for <see cref="BatchProcessor"/> focusing on file-not-found and error-handling
/// behavior. Engine-dependent tests (full pipeline) are covered by integration/smoke tests
/// since <see cref="TrackdubDubbingEngine"/> is sealed and <see cref="TrackdubSessionFactory"/>
/// has an internal constructor.
///
/// These tests exercise:
/// - Fail-fast halt on first missing file (remaining marked Skipped)
/// - Continue-on-error with multiple missing files
/// - Report count accuracy
/// - Empty file list handling
/// </summary>
public sealed class BatchProcessorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BatchProcessor _processor;

    public BatchProcessorTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), $"trackdub-batch-proc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // We need a real engine instance. Use TrackdubBuilder to create a minimal one.
        // The engine will never actually execute because all files will be non-existent
        // (BatchProcessor checks File.Exists before calling engine.ExecuteAsync).
        using var factory = new TrackdubBuilder().Build();
        _processor = new BatchProcessor(new TrackdubDubbingEngine(factory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static DubbingSessionOptions CreateTemplateOptions() => new()
    {
        SourceMediaPath = "placeholder.mp4",
        TargetLanguageCode = "es",
    };

    // ─── Fail-fast: first file not found, rest skipped ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_FailFast_FirstFileNotFound_RemainingSkipped()
    {
        var files = new[]
        {
            Path.Join(_tempDir, "missing1.mp4"),
            Path.Join(_tempDir, "missing2.mp4"),
            Path.Join(_tempDir, "missing3.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(2, report.SkippedCount);

        // First file = Failed
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Contains("not found", report.Files[0].Reason, StringComparison.OrdinalIgnoreCase);

        // Remaining = Skipped
        Assert.Equal(BatchFileStatus.Skipped, report.Files[1].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[2].Status);
    }

    [Fact]
    public async Task ExecuteAsync_FailFast_SingleFileNotFound_ReportsCorrectly()
    {
        var files = new[] { Path.Join(_tempDir, "only-one-missing.mp4") };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Single(report.Files);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.NotNull(report.Files[0].Reason);
    }

    // ─── Per-file boundaries: pre-engine failures must still advance the queue ────────────

    [Fact]
    public async Task ExecuteAsync_BatchProgress_ReportsBoundariesForFileThatFailsBeforeEngine()
    {
        var recording = new RecordingEngine();
        var processor = new BatchProcessor(recording);
        string missing = Path.Join(_tempDir, "missing-before-engine.mp4");
        string existing = WriteMedia("existing-before-engine.mp4");
        var events = new List<BatchFileProgressEvent>();
        var batchProgress = new Progress<BatchFileProgressEvent>(events.Add);

        BatchReport report = await processor.ExecuteAsync(
            [missing, existing],
            CreateTemplateOptions(),
            new BatchOptions { ContinueOnError = true },
            progress: null,
            ct: CancellationToken.None,
            batchProgress: batchProgress);

        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(BatchFileStatus.Success, report.Files[1].Status);
        Assert.Contains(events, e => e.FileIndex == 0 && e.Kind == BatchFileProgressKind.Started);
        Assert.Contains(events, e => e.FileIndex == 0 && e.Kind == BatchFileProgressKind.Failed);
        Assert.Contains(events, e => e.FileIndex == 1 && e.Kind == BatchFileProgressKind.Started);
        Assert.Contains(events, e => e.FileIndex == 1 && e.Kind == BatchFileProgressKind.Succeeded);
        Assert.Equal(existing, Assert.Single(recording.Options).SourceMediaPath);
    }

    [Fact]
    public async Task ExecuteAsync_BatchProgress_ReportsBoundariesForLockedFileBeforeEngine()
    {
        var recording = new RecordingEngine();
        var processor = new BatchProcessor(recording);
        string locked = WriteMedia("locked-before-engine.mp4");
        string existing = WriteMedia("existing-after-locked-before-engine.mp4");
        using ProjectLock holder = ProjectLock.Acquire(ProjectDirectoryOf(locked));
        var events = new List<BatchFileProgressEvent>();
        var batchProgress = new Progress<BatchFileProgressEvent>(events.Add);

        BatchReport report = await processor.ExecuteAsync(
            [locked, existing],
            CreateTemplateOptions(),
            new BatchOptions { ContinueOnError = true },
            progress: null,
            ct: CancellationToken.None,
            batchProgress: batchProgress);

        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(BatchFileStatus.Success, report.Files[1].Status);
        Assert.Contains(events, e => e.FileIndex == 0 && e.Kind == BatchFileProgressKind.Started);
        Assert.Contains(events, e => e.FileIndex == 0 && e.Kind == BatchFileProgressKind.Failed);
        Assert.Contains(events, e => e.FileIndex == 1 && e.Kind == BatchFileProgressKind.Started);
        Assert.Contains(events, e => e.FileIndex == 1 && e.Kind == BatchFileProgressKind.Succeeded);
        Assert.Equal(existing, Assert.Single(recording.Options).SourceMediaPath);
    }

    // ─── Continue-on-error: all files not found ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ContinueOnError_AllFilesNotFound_AllFailed()
    {
        var files = new[]
        {
            Path.Join(_tempDir, "a-missing.mp4"),
            Path.Join(_tempDir, "b-missing.mp4"),
            Path.Join(_tempDir, "c-missing.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = true };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(3, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);

        // All three should be Failed (not Skipped) because continue-on-error
        Assert.All(report.Files, f =>
        {
            Assert.Equal(BatchFileStatus.Failed, f.Status);
            Assert.NotNull(f.Reason);
            Assert.Contains("not found", f.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ─── Continue-on-error: mix of missing files ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ContinueOnError_MultipleFailures_NoneSkipped()
    {
        var files = new[]
        {
            Path.Join(_tempDir, "x-missing.mp4"),
            Path.Join(_tempDir, "y-missing.wav"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = true };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(2, report.Files.Count);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
    }

    // ─── Report counts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReportCounts_SumToTotalFileCount()
    {
        var files = new[]
        {
            Path.Join(_tempDir, "f1.mp4"),
            Path.Join(_tempDir, "f2.mp4"),
            Path.Join(_tempDir, "f3.mp4"),
            Path.Join(_tempDir, "f4.mp4"),
            Path.Join(_tempDir, "f5.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        int total = report.SucceededCount + report.FailedCount + report.SkippedCount;
        Assert.Equal(files.Length, total);
        Assert.Equal(files.Length, report.Files.Count);
    }

    // ─── File paths preserved in report ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FilePathsPreserved_InReport()
    {
        var files = new[]
        {
            Path.Join(_tempDir, "alpha.mp4"),
            Path.Join(_tempDir, "beta.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = true };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(files[0], report.Files[0].FilePath);
        Assert.Equal(files[1], report.Files[1].FilePath);
    }

    // ─── Empty file list ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyFileList_ReturnsEmptyReport()
    {
        var files = Array.Empty<string>();
        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Empty(report.Files);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
    }

    // ─── Null argument validation ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullMediaFiles_ThrowsArgumentNullException()
    {
        var batchOptions = new BatchOptions { ContinueOnError = false };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ExecuteAsync(null!, CreateTemplateOptions(), batchOptions, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NullTemplateOptions_ThrowsArgumentNullException()
    {
        var files = new[] { "file.mp4" };
        var batchOptions = new BatchOptions { ContinueOnError = false };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ExecuteAsync(files, null!, batchOptions, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NullBatchOptions_ThrowsArgumentNullException()
    {
        var files = new[] { "file.mp4" };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ExecuteAsync(files, CreateTemplateOptions(), null!, null, CancellationToken.None));
    }

    // ─── Fail-fast with second file missing ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FailFast_SkippedFilesHaveReasonString()
    {
        var files = new[]
        {
            Path.Join(_tempDir, "first-missing.mp4"),
            Path.Join(_tempDir, "second-would-skip.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        // Skipped files get a reason explaining the skip
        var skipped = report.Files[1];
        Assert.Equal(BatchFileStatus.Skipped, skipped.Status);
        Assert.NotNull(skipped.Reason);
        Assert.Contains("fail-fast", skipped.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Foreign-token OperationCanceledException must not escape ───────────────

    [Fact]
    public async Task ExecuteAsync_ForeignTokenOperationCanceled_DoesNotEscape_ReturnsReport()
    {
        // A cancellation token that is NOT the batch token, and is never requested.
        using var foreignCts = new CancellationTokenSource();
        var engine = new ThrowingEngine(new OperationCanceledException(foreignCts.Token));
        var processor = new BatchProcessor(engine);

        string existingFile = Path.Join(_tempDir, "exists.mp4");
        File.WriteAllBytes(existingFile, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);

        // ct is None (not requested), so the OCE with the foreign token matches the
        // generic Exception handler's guard negatively and must hit the OCE fallback.
        BatchReport report = await processor.ExecuteAsync(
            [existingFile], CreateTemplateOptions(), new BatchOptions { ContinueOnError = false },
            progress: null, CancellationToken.None);

        Assert.Single(report.Files);
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
    }

    [Fact]
    public async Task ExecuteAsync_ForeignTokenOperationCanceled_ContinueOnError_AllFailed()
    {
        using var foreignCts = new CancellationTokenSource();
        var engine = new ThrowingEngine(new OperationCanceledException(foreignCts.Token));
        var processor = new BatchProcessor(engine);

        var files = new[]
        {
            Path.Join(_tempDir, "a.mp4"),
            Path.Join(_tempDir, "b.mp4"),
            Path.Join(_tempDir, "c.mp4"),
        };
        foreach (string f in files)
        {
            File.WriteAllBytes(f, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);
        }

        BatchReport report = await processor.ExecuteAsync(
            files, CreateTemplateOptions(), new BatchOptions { ContinueOnError = true },
            progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(3, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
        Assert.All(report.Files, f => Assert.Equal(BatchFileStatus.Failed, f.Status));
    }

    [Fact]
    public async Task ExecuteAsync_ForeignTokenOperationCanceled_FailFast_RemainingSkipped()
    {
        using var foreignCts = new CancellationTokenSource();
        var engine = new ThrowingEngine(new OperationCanceledException(foreignCts.Token));
        var processor = new BatchProcessor(engine);

        var files = new[]
        {
            Path.Join(_tempDir, "first.mp4"),
            Path.Join(_tempDir, "second.mp4"),
            Path.Join(_tempDir, "third.mp4"),
        };
        foreach (string f in files)
        {
            File.WriteAllBytes(f, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);
        }

        BatchReport report = await processor.ExecuteAsync(
            files, CreateTemplateOptions(), new BatchOptions { ContinueOnError = false },
            progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(2, report.SkippedCount);
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[1].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[2].Status);
    }

    // ─── Project lock: batch runs are mutually excluded with CLI and desktop runs ──

    [Fact]
    public async Task ExecuteAsync_LockedProject_FailsThatFileNamingTheHolder_AndContinues()
    {
        var recording = new RecordingEngine();
        var processor = new BatchProcessor(recording);
        string locked = WriteMedia("locked.mp4");
        string free = WriteMedia("free.mp4");
        using ProjectLock holder = ProjectLock.Acquire(ProjectDirectoryOf(locked));

        BatchReport report = await processor.ExecuteAsync(
            [locked, free], CreateTemplateOptions(), new BatchOptions { ContinueOnError = true },
            progress: null, CancellationToken.None);

        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Contains(
            $"locked by process {Environment.ProcessId}",
            report.Files[0].Reason,
            StringComparison.Ordinal);
        Assert.Equal(BatchFileStatus.Success, report.Files[1].Status);
        Assert.Equal(free, Assert.Single(recording.Options).SourceMediaPath);
    }

    [Fact]
    public async Task ExecuteAsync_LockedProject_FailFast_SkipsTheRest()
    {
        var recording = new RecordingEngine();
        var processor = new BatchProcessor(recording);
        string locked = WriteMedia("locked.mp4");
        string next = WriteMedia("next.mp4");
        using ProjectLock holder = ProjectLock.Acquire(ProjectDirectoryOf(locked));

        BatchReport report = await processor.ExecuteAsync(
            [locked, next], CreateTemplateOptions(), new BatchOptions { ContinueOnError = false },
            progress: null, CancellationToken.None);

        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[1].Status);
        Assert.Empty(recording.Options);
    }

    [Fact]
    public async Task ExecuteAsync_HoldsTheProjectLockOnlyWhileThatFileRuns()
    {
        string first = WriteMedia("first.mp4");
        string second = WriteMedia("second.mp4");
        var lockedDuringRun = new List<bool>();
        bool? firstLockedWhileSecondRuns = null;
        var recording = new RecordingEngine
        {
            OnExecute = options =>
            {
                lockedDuringRun.Add(IsLocked(ProjectDirectoryOf(options.SourceMediaPath)));
                if (options.SourceMediaPath == second)
                {
                    firstLockedWhileSecondRuns = IsLocked(ProjectDirectoryOf(first));
                }
            }
        };
        var processor = new BatchProcessor(recording);

        BatchReport report = await processor.ExecuteAsync(
            [first, second], CreateTemplateOptions(), new BatchOptions { ContinueOnError = true },
            progress: null, CancellationToken.None);

        Assert.Equal(2, report.SucceededCount);
        Assert.Equal([true, true], lockedDuringRun);
        Assert.False(firstLockedWhileSecondRuns);
        Assert.False(IsLocked(ProjectDirectoryOf(first)));
        Assert.False(IsLocked(ProjectDirectoryOf(second)));
    }

    private string WriteMedia(string name)
    {
        string path = Path.Join(_tempDir, name);
        File.WriteAllBytes(path, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);
        return path;
    }

    private static string ProjectDirectoryOf(string mediaPath) =>
        TrackdubProjectPaths.ResolveProjectDirectory(mediaPath, CreateTemplateOptions().ProjectOutputDirectory);

    private static bool IsLocked(string projectDirectory)
    {
        try
        {
            using ProjectLock probe = ProjectLock.Acquire(projectDirectory);
            return false;
        }
        catch (ProjectLockedException)
        {
            return true;
        }
    }

    /// <summary>
    /// Minimal engine double that throws a fixed exception from <see cref="ExecuteAsync"/>,
    /// used to exercise <see cref="BatchProcessor"/> error handling without a real pipeline.
    /// </summary>
    private sealed class ThrowingEngine : IDubbingPipelineEngine
    {
        private readonly Exception _exception;

        public ThrowingEngine(Exception exception) => _exception = exception;

        public Task<DubbingRunResult> ExecuteAsync(
            DubbingSessionOptions options,
            IProgress<PipelineProgressEvent>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }

    // ─── Host-supplied engine accessibility ─────────────────────────────────────
    //
    // Regression cover for the desktop shell (Trackdub-gated G1). The desktop registers its
    // own IDubbingPipelineEngine in its DI container so the batch lane shares the host's
    // session factory, model-selection providers, and licensing. Before BatchProcessor's
    // IDubbingPipelineEngine overload was public, the only public constructor demanded the
    // sealed TrackdubDubbingEngine, whose TrackdubSessionFactory dependency has an internal
    // constructor reachable only via TrackdubBuilder — forcing the desktop to build a second
    // headless container that silently bypasses every override the host registered.
    //
    // NOTE: the behavioural tests below cannot detect the constructor being narrowed back to
    // internal, because this project holds InternalsVisibleTo for Trackdub.Sdk and can call an
    // internal constructor. Verified by negative control: reverting the ctor to internal left
    // this file compiling and passing. The real enforcement is the reflection-based
    // Constructor_IsPublic_* tests plus the desktop's own call site, which has no such grant.
    // Do not "simplify" the behaviour tests into accessibility coverage.

    [Fact]
    public void Constructor_AcceptsHostSuppliedEngine_WithoutTrackdubDubbingEngine()
    {
        IDubbingPipelineEngine hostEngine = new ThrowingEngine(new InvalidOperationException("unused"));

        // No cast to the sealed SDK engine, and no TrackdubBuilder container.
        var processor = new BatchProcessor(hostEngine);

        Assert.NotNull(processor);
    }

    [Fact]
    public void Constructor_HostSuppliedEngine_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new BatchProcessor((IDubbingPipelineEngine)null!));
    }

    [Fact]
    public async Task Constructor_HostSuppliedEngine_DrivesTheBatchRun()
    {
        // Proves the injected instance is the one actually executed, not a wrapped copy.
        var recording = new RecordingEngine();
        var processor = new BatchProcessor((IDubbingPipelineEngine)recording);

        string existing = Path.Join(_tempDir, "recorded.mp4");
        File.WriteAllBytes(existing, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);

        BatchReport report = await processor.ExecuteAsync(
            [existing], CreateTemplateOptions(), new BatchOptions { ContinueOnError = true },
            progress: null, CancellationToken.None);

        Assert.Single(recording.Options);
        Assert.Equal(existing, recording.Options[0].SourceMediaPath);
        Assert.Equal(1, report.SucceededCount);
        Assert.Equal(BatchFileStatus.Success, report.Files[0].Status);
    }

    // ─── Accessibility: asserted reflectively so InternalsVisibleTo cannot mask it ──

    [Fact]
    public void Constructor_TakingIDubbingPipelineEngine_IsPublic()
    {
        ConstructorInfo? ctor = typeof(BatchProcessor)
            .GetConstructor([typeof(IDubbingPipelineEngine)]);

        Assert.NotNull(ctor);
        Assert.True(
            ctor.IsPublic,
            "BatchProcessor(IDubbingPipelineEngine) must stay public. Trackdub.Sdk.Tests holds "
            + "InternalsVisibleTo, so this assertion — not the behaviour tests above — is what "
            + "detects the constructor being narrowed, which would break UI hosts such as the "
            + "Trackdub-gated desktop shell that have no such grant.");
    }

    [Fact]
    public void BatchOutputPaths_IsPublicStaticWithPublicEntryPoints()
    {
        // The desktop report view resolves per-file output directories so it shows where each
        // file actually landed. Keep the type and its two entry points reachable.
        Type type = typeof(BatchOutputPaths);

        Assert.True(type.IsPublic, "BatchOutputPaths must stay public for host batch UIs.");
        Assert.True(type.IsAbstract && type.IsSealed, "BatchOutputPaths must remain a static class.");

        foreach (string name in new[] { "BuildProjectDirectory", "BuildUniqueProjectFolderName" })
        {
            MethodInfo? method = type.GetMethod(
                name,
                BindingFlags.Public | BindingFlags.Static);
            Assert.True(method is not null, $"BatchOutputPaths.{name} must be public static.");
        }
    }

    /// <summary>
    /// Engine double that records the options it was handed and reports success, so tests can
    /// assert the host-supplied instance is the one the batch loop drives.
    /// </summary>
    private sealed class RecordingEngine : IDubbingPipelineEngine
    {
        public List<DubbingSessionOptions> Options { get; } = [];

        public Action<DubbingSessionOptions>? OnExecute { get; init; }

        public Task<DubbingRunResult> ExecuteAsync(
            DubbingSessionOptions options,
            IProgress<PipelineProgressEvent>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);
            OnExecute?.Invoke(options);
            return Task.FromResult(SuccessResult());
        }

        private static DubbingRunResult SuccessResult() => new()
        {
            RunId = Guid.NewGuid(),
            StartTime = DateTimeOffset.UnixEpoch,
            EndTime = DateTimeOffset.UnixEpoch,
            OverallStatus = DubbingRunStatus.Succeeded,
            StageOutcomes = [],
        };
    }
}
