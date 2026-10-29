using FsCheck;
using FsCheck.Xunit;
using Microsoft.Extensions.DependencyInjection;
using Trackdub.Contracts.Dubbing;
using Trackdub.Sdk;
using Trackdub.Sdk.Composition;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Property-based tests verifying cancellation propagation: passing a cancelled
/// <see cref="CancellationToken"/> results in <see cref="OperationCanceledException"/>
/// without partial state corruption.
///
/// **Validates: Requirements 4.6**
/// </summary>
public sealed class CancellationPropagationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];
    private readonly List<string> _tempFiles = [];

    /// <summary>
    /// Property 8: Cancellation propagation — when a pre-cancelled CancellationToken is passed
    /// to ExecuteAsync with a valid media file, the engine throws OperationCanceledException
    /// (from the pre-flight checker) or returns a result where no stage succeeded.
    /// No pipeline stage produces artifacts when cancellation is requested.
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 30)]
    public bool PreCancelledToken_WithValidMedia_ThrowsOrReturnsNoSucceededStages(PositiveInt seed)
    {
        // Arrange: create a valid temp media file so we pass the media-exists validation
        string tempMediaFile = CreateTempMediaFile();
        string outputDir = CreateTempProjectDir();

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = tempMediaFile,
            ProjectOutputDirectory = outputDir,
            TargetLanguageCode = "es",
            ForceRerun = true,
        };

        using var factory = CreateFactory();
        var engine = new TrackdubDubbingEngine(factory);

        // Act: pass an already-cancelled token
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool threwOce = false;
        DubbingRunResult? result = null;

        try
        {
            result = engine.ExecuteAsync(options, progress: null, cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            threwOce = true;
        }

        // Assert: either OCE was thrown (pre-flight propagated cancellation)
        // or a result was returned with cancelled stages — both are valid cancellation behaviors.
        if (threwOce)
        {
            // OperationCanceledException thrown = correct cancellation propagation.
            // No pipeline stage ran, so no partial pipeline state corruption is possible.
            return true;
        }

        // If a result was returned (e.g., pre-flight checker not available),
        // verify stages are properly cancelled
        if (result is not null)
        {
            foreach (StageOutcome outcome in result.StageOutcomes)
            {
                // No stage should have succeeded when token was pre-cancelled
                if (outcome.Status == StageStatus.Succeeded)
                    return false;

                // Cancelled stages must have empty artifacts (no partial state)
                if (outcome.ReasonCode == "CANCELLED" && outcome.ArtifactPaths.Count > 0)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Property 8 (continued): When a pre-cancelled token is passed with a non-existent media file,
    /// the engine returns a Failed result (media validation happens before cancellation check in the
    /// stage loop). The result is well-formed with no partial state corruption.
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 30)]
    public bool PreCancelledToken_WithMissingMedia_ReturnsFailedResult(NonEmptyString targetLang)
    {
        // Arrange: use a non-existent media path
        string nonExistentPath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".mp4");

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = nonExistentPath,
            TargetLanguageCode = SanitizeLanguageCode(targetLang.Get),
        };

        using var factory = CreateFactory();
        var engine = new TrackdubDubbingEngine(factory);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        DubbingRunResult result = engine.ExecuteAsync(options, progress: null, cts.Token)
            .GetAwaiter().GetResult();

        // Assert: media validation should fail before any stage runs
        if (result is null)
            return false;

        if (result.OverallStatus != DubbingRunStatus.Failed)
            return false;

        // No stages should have executed
        if (result.StageOutcomes.Count != 0)
            return false;

        // Result should be well-formed
        if (result.RunId == Guid.Empty)
            return false;

        if (result.EndTime < result.StartTime)
            return false;

        return true;
    }

    /// <summary>
    /// Property 8 (continued): Cancellation propagation is deterministic — for any valid
    /// DubbingSessionOptions with a pre-cancelled token, the engine either throws
    /// OperationCanceledException or returns a result where no stage succeeded.
    /// No partial state corruption occurs in either case.
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 30)]
    public bool CancelledRun_NeverProducesSucceededStages(PositiveInt seed)
    {
        // Arrange
        string tempMediaFile = CreateTempMediaFile();
        string outputDir = CreateTempProjectDir();

        var options = new DubbingSessionOptions
        {
            SourceMediaPath = tempMediaFile,
            ProjectOutputDirectory = outputDir,
            TargetLanguageCode = "fr",
            ForceRerun = true,
        };

        using var factory = CreateFactory();
        var engine = new TrackdubDubbingEngine(factory);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        DubbingRunResult? result = null;
        bool threwOce = false;

        try
        {
            result = engine.ExecuteAsync(options, progress: null, cts.Token)
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            threwOce = true;
        }

        // Assert
        if (threwOce)
        {
            // OCE thrown = cancellation propagated correctly, no partial result
            return true;
        }

        if (result is null)
            return false;

        // If a result was returned, verify no stage succeeded and no partial artifacts
        foreach (StageOutcome outcome in result.StageOutcomes)
        {
            // No stage should have succeeded
            if (outcome.Status == StageStatus.Succeeded)
                return false;

            // Cancelled stages must have empty artifact paths (no partial state corruption)
            if (outcome.ReasonCode == "CANCELLED")
            {
                if (outcome.ArtifactPaths is null || outcome.ArtifactPaths.Count > 0)
                    return false;
            }

            // All outcomes must have valid structure
            if (string.IsNullOrWhiteSpace(outcome.StageName))
                return false;

            if (outcome.EndTime < outcome.StartTime)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically,
    /// ensuring test discovery works with xunit.runner.visualstudio v3.
    /// Tests the cancellation propagation property.
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Fact]
    public void CancellationPropagation_PropertyCheck_ViaFact()
    {
        // One factory for every case: the property is about run cancellation, not factory
        // construction, and rebuilding the headless composition per case dominated the runtime.
        using var factory = CreateFactory();
        Prop.ForAll<PositiveInt>(seed =>
        {
            string tempMediaFile = CreateTempMediaFile();
            string outputDir = CreateTempProjectDir();

            var options = new DubbingSessionOptions
            {
                SourceMediaPath = tempMediaFile,
                ProjectOutputDirectory = outputDir,
                TargetLanguageCode = "de",
                ForceRerun = true,
            };

            var engine = new TrackdubDubbingEngine(factory);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            DubbingRunResult? result = null;
            bool threwOce = false;

            try
            {
                result = engine.ExecuteAsync(options, progress: null, cts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                threwOce = true;
            }

            if (threwOce)
            {
                // OCE thrown = correct cancellation propagation, no partial state
                return true.ToProperty();
            }

            // If result returned, verify no stage succeeded and no partial artifacts
            bool noSucceeded = result!.StageOutcomes.All(o => o.Status != StageStatus.Succeeded);

            bool noCancelledWithArtifacts = result.StageOutcomes
                .Where(o => o.ReasonCode == "CANCELLED")
                .All(o => o.ArtifactPaths.Count == 0);

            bool timingOk = result.EndTime >= result.StartTime;

            return noSucceeded
                .And(noCancelledWithArtifacts)
                .And(timingOk);
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

    private string CreateTempMediaFile()
    {
        string dir = Path.Join(Path.GetTempPath(), "TrackdubTests");
        Directory.CreateDirectory(dir);
        string filePath = Path.Join(dir, Guid.NewGuid().ToString("N") + ".mp4");
        // Create a minimal file so File.Exists returns true
        File.WriteAllBytes(filePath, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);
        _tempFiles.Add(filePath);
        if (!_tempDirs.Contains(dir))
            _tempDirs.Add(dir);
        return filePath;
    }

    private static string SanitizeLanguageCode(string input)
    {
        // Ensure we have a reasonable language code for the test
        if (string.IsNullOrWhiteSpace(input))
            return "es";

        // Take first 5 chars max to keep it BCP-47 like
        string sanitized = new(input.Where(c => char.IsLetterOrDigit(c) || c == '-').Take(5).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "es" : sanitized;
    }

    public void Dispose()
    {
        foreach (string file in _tempFiles)
        {
            try { File.Delete(file); }
            catch { /* best-effort cleanup */ }
        }

        foreach (string dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }
    }
}
