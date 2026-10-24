using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Captures an EP-context artifact while a TensorRT RTX session builds its engines from the source
/// graph, so later processes deserialize the engines instead of rebuilding them. The capture rides
/// on the build the session performs anyway; only writing the engine is extra. Measured on
/// MADLAD-400 3B fp16 (RTX 5070): the encoder session takes ~48 s to build from source and 3.6 s to
/// load from its EP-context artifact.
/// </summary>
internal static class EpContextCapture
{
    /// <summary>Below this many model bytes, engine builds are short and an artifact saves little.</summary>
    internal const long MinimumSourceBytes = 256L * 1024 * 1024;

    private static readonly ConcurrentDictionary<string, string> FailedSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reasons a capture was not attempted. <see cref="InsufficientDiskSpace"/> is observed
    /// per call and never memoized: free space can change between sessions (a disk that was
    /// full during one launch may be fine the next), so it must not permanently disable
    /// capture. Every other reason reflects a property of the graph/capture itself and is
    /// sticky for the process.
    /// </summary>
    private static readonly ConcurrentDictionary<string, string> SkippedSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reason used when capture is skipped purely for lack of free disk space.</summary>
    public const string InsufficientDiskSpaceReason =
        "Insufficient free disk space for the EP-context artifact; engines will rebuild on every launch.";

    internal static bool ShouldCapture(string sourceModelPath) =>
        TryGetSkipReason(sourceModelPath, out _) is false;

    /// <summary>
    /// Explains why <paramref name="sourceModelPath"/> will not have its engines captured, or
    /// returns <see langword="false"/> when capture will be attempted. Operators need this
    /// surfaced: a silently skipped capture means every future launch rebuilds the engines.
    /// </summary>
    internal static bool TryGetSkipReason(string sourceModelPath, out string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceModelPath);

        string fullPath = Path.GetFullPath(sourceModelPath);
        if (EpContextArtifact.IsEpContextPath(fullPath))
        {
            reason = "Already an EP-context artifact.";
            return true;
        }

        if (FailedSources.TryGetValue(fullPath, out string? failed))
        {
            reason = failed;
            return true;
        }

        if (SkippedSources.TryGetValue(fullPath, out string? skipped))
        {
            reason = skipped;
            return true;
        }

        // Size is a property of the graph, so it is sticky and checked before headroom: an
        // artifact that would never be captured should not be described as a space problem.
        long sourceBytes = EpContextArtifact.GetSourceTotalBytes(fullPath);
        if (sourceBytes < MinimumSourceBytes)
        {
            const string smallGraphReason =
                "Graph is small enough that an engine build is cheaper than an artifact.";
            SkippedSources.TryAdd(fullPath, smallGraphReason);
            reason = smallGraphReason;
            return true;
        }

        // Free-space outcomes are recomputed every call so recovering disk space re-enables
        // capture without an app restart, and are never recorded in the sticky skip snapshot.
        // The engine roughly mirrors the weights; keep at least that much free again afterwards.
        if (!HasFreeSpace(fullPath, sourceBytes * 2))
        {
            reason = InsufficientDiskSpaceReason;
            return true;
        }

        reason = string.Empty;
        return false;
    }

    /// <summary>
    /// Snapshot of capture decisions made in this process, for diagnostics and doctor output.
    /// Keys are model paths; values explain why capture was skipped or failed.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> SnapshotCaptureSkips()
    {
        var snapshot = new Dictionary<string, string>(SkippedSources, StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string reason) in FailedSources)
        {
            snapshot[path] = reason;
        }

        return snapshot;
    }

    /// <summary>
    /// Creates the session with EP-context generation enabled, then publishes the artifact next to
    /// <paramref name="sourceModelPath"/>. A capture problem never costs the session: creation is
    /// retried once without capture for capture I/O failures; native engine failures propagate.
    /// </summary>
    internal static InferenceSession CreateSession(
        string sourceModelPath,
        SessionOptions options,
        IReadOnlyDictionary<string, string> effectiveTrtOptions,
        Func<string, SessionOptions, InferenceSession> createSession)
    {
        string fullSource = Path.GetFullPath(sourceModelPath);
        string epContextPath = EpContextArtifact.GetEpContextPath(fullSource);
        string stagingDirectory = Path.Join(
            Path.GetDirectoryName(epContextPath)!,
            ".epc-capture-" + Guid.NewGuid().ToString("N"));

        InferenceSession session;
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            options.AddSessionConfigEntry("ep.context_enable", "1");
            options.AddSessionConfigEntry("ep.context_file_path", Path.Join(stagingDirectory, Path.GetFileName(epContextPath)));
            options.AddSessionConfigEntry("ep.context_embed_mode", EpContextArtifact.ShouldEmbedEpContext(fullSource) ? "1" : "0");
            session = createSession(sourceModelPath, options);
        }
        catch (Exception ex) when (ShouldRetryWithoutCapture(ex))
        {
            MarkFailed(fullSource, ex.Message);
            EpContextCompiler.TryDeleteDirectory(stagingDirectory);
            options.AddSessionConfigEntry("ep.context_enable", "0");
            return createSession(sourceModelPath, options);
        }
        catch (OnnxRuntimeException ex)
        {
            MarkFailed(fullSource, ex.Message);
            EpContextCompiler.TryDeleteDirectory(stagingDirectory);
            throw;
        }

        try
        {
            Publish(fullSource, stagingDirectory, effectiveTrtOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MarkFailed(fullSource, ex.Message);
        }
        finally
        {
            EpContextCompiler.TryDeleteDirectory(stagingDirectory);
        }

        return session;
    }

    private static void Publish(
        string sourceModelPath,
        string stagingDirectory,
        IReadOnlyDictionary<string, string> effectiveTrtOptions)
    {
        string stagedModel = Path.Join(stagingDirectory, Path.GetFileName(EpContextArtifact.GetEpContextPath(sourceModelPath)));
        // A graph the EP did not claim yields a plain reserialized model that would not skip the
        // engine build; keep nothing rather than an artifact that only adds parse overhead.
        if (!File.Exists(stagedModel) || !EpContextCompiler.TryContainsEpContextNodes(stagedModel))
        {
            MarkFailed(sourceModelPath, "Session creation produced no EP-context nodes.");
            return;
        }

        // Loads reject an artifact that is not optimal for the session's device; publishing one
        // would replace a usable artifact with one every later session rebuilds past.
        if (!EpContextCompatibility.AllowsTensorRtRtxArtifact(stagedModel))
        {
            MarkFailed(sourceModelPath, "Captured artifact is not EP_SUPPORTED_OPTIMAL for the selected TensorRT-RTX device.");
            return;
        }

        (string gpuArchitecture, string? driverVersion) = EpContextLoadPathResolver.CurrentHardware;
        string compileOptionsIdentity = EpContextLoadPathResolver.BuildCompileOptionsIdentity(sourceModelPath, effectiveTrtOptions);
        EpContextArtifact.PublishStagedArtifact(stagingDirectory, sourceModelPath,
            existingArtifactUsable: EpContextCompatibility.AllowsTensorRtRtxArtifact,
            createStamp: artifactFiles => EpContextArtifact.CreateStamp(
                sourceModelPath,
                new FileInfo(sourceModelPath),
                sourceSha256: null,
                gpuArchitecture,
                driverVersion,
                artifactFiles,
                compileOptionsIdentity: compileOptionsIdentity));
    }

    internal static bool ShouldRetryWithoutCapture(Exception exception) =>
        exception is IOException or UnauthorizedAccessException ||
        (exception is OnnxRuntimeException && IsCaptureWriteFailure(exception.Message));

    internal static bool IsCaptureWriteFailure(string message) =>
        message.Contains("ep.context", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("EP-context", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("EPContext", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("failed to write", StringComparison.OrdinalIgnoreCase);

    private static void MarkFailed(string sourceModelPath, string reason)
    {
        FailedSources[sourceModelPath] = reason;
        Trace.TraceWarning($"EP-context capture skipped for '{sourceModelPath}': {reason}");
    }

    private static bool HasFreeSpace(string path, long requiredBytes)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            return root is not null && new DriveInfo(root).AvailableFreeSpace >= requiredBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // An inaccessible drive is treated as insufficient space: capture is optional.
            return false;
        }
    }
}
