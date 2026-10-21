using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Contracts.Jobs;

namespace Trackdub.Cli;

internal static class CliMachineResultWriter
{
    internal static async Task WriteAsync(WorkerRunOutcome outcome, string path, CancellationToken token)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, outcome, WorkerRunOutcome.JsonOptions, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: false);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static async Task<WorkerArtifact> DescribeAsync(string root, string path, string kind, CancellationToken token)
    {
        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Join(fullRoot, path));
        string relative = Path.GetRelativePath(fullRoot, fullPath);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new IOException("Artifact escaped the project directory.");
        for (string? part = fullPath; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Artifact paths cannot contain symbolic links.");
            if (string.Equals(part, fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        await using var stream = File.OpenRead(fullPath);
        if (stream.Length == 0) throw new IOException("Artifact is empty.");
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        return new WorkerArtifact(relative.Replace('\\', '/'), kind, stream.Length, hash);
    }
}
