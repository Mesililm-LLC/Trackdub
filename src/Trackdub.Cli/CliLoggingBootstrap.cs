using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliLoggingBootstrap
{
    internal static void EnsureReady(TrackdubSessionFactory factory)
    {
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        IApplicationLogger logger = factory.GetRequiredService<IApplicationLogger>();

        string logPath = storagePaths.LogFilePath;
        string? logDirectory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        EnsureLogFileExists(logPath);

        logger.LogInformation("Trackdub CLI started.");
    }

    /// <summary>
    /// Creates the log file if it is missing. Another process, or another session in this one, can hold or
    /// rotate the file while this runs; a sharing violation on a file that exists means the log is already
    /// there and in use, which is all this step needs.
    /// </summary>
    internal static void EnsureLogFileExists(string logPath)
    {
        if (File.Exists(logPath))
        {
            return;
        }

        try
        {
            using FileStream _ = File.Open(
                logPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
        }
        catch (IOException) when (File.Exists(logPath))
        {
            // Created or locked by a concurrent session between the check and the open.
        }
    }
}
