namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Filesystem snapshot + outcome classification for the classic TensorRT engine cache.
/// Unlike <see cref="EngineCacheProbe.ClassifyOutcome"/> (which refuses to claim a hit
/// without per-model evidence), a warm pass created the session itself against this
/// exact cache directory, so growth means compiled and a populated cache with no
/// growth means the engines were reused.
/// </summary>
internal static class ClassicTensorRtCacheClassifier
{
    public static EngineCacheProbe.Snapshot CaptureSnapshot(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return new EngineCacheProbe.Snapshot(directoryPath, DirectoryExists: false, FileCount: 0, TotalBytes: 0);
        }

        int fileCount = 0;
        long totalBytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                totalBytes += new FileInfo(file).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: classification must never fail a warm pass.
        }

        return new EngineCacheProbe.Snapshot(directoryPath, DirectoryExists: true, fileCount, totalBytes);
    }

    /// <summary>Returns "compiled", "reused", or "failed" (session ran but nothing was cached).</summary>
    public static string Classify(EngineCacheProbe.Snapshot before, EngineCacheProbe.Snapshot after)
    {
        long bytesAdded = Math.Max(0, after.TotalBytes - before.TotalBytes);
        int filesAdded = Math.Max(0, after.FileCount - before.FileCount);

        if (bytesAdded > 0 || filesAdded > 0)
        {
            return "compiled";
        }

        if (before.FileCount > 0)
        {
            return "reused";
        }

        return "failed";
    }
}
