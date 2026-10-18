namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Filesystem snapshot + outcome classification for the classic TensorRT engine cache.
/// Only nonempty engines bearing the warmed graph's cache prefix are evidence.
/// Files belonging to other graphs, timing caches and profiles cannot prove readiness.
/// </summary>
internal static class ClassicTensorRtCacheClassifier
{
    public static EngineCacheProbe.Snapshot CaptureSnapshot(string directoryPath, string filePrefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePrefix);
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
                if (!Path.GetFileName(file).StartsWith(filePrefix, StringComparison.Ordinal)
                    || !string.Equals(Path.GetExtension(file), ".engine", StringComparison.Ordinal))
                {
                    continue;
                }

                long length = new FileInfo(file).Length;
                if (length > 0)
                {
                    fileCount++;
                    totalBytes += length;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Incomplete evidence must not produce a successful readiness claim.
            fileCount = 0;
            totalBytes = 0;
        }

        return new EngineCacheProbe.Snapshot(directoryPath, DirectoryExists: true, fileCount, totalBytes);
    }

    /// <summary>Returns "compiled", "reused", or "failed" (session ran but nothing was cached).</summary>
    public static string Classify(EngineCacheProbe.Snapshot before, EngineCacheProbe.Snapshot after)
    {
        if (after.FileCount == 0 || after.TotalBytes == 0)
        {
            return "failed";
        }
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
