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

        // Do not open the log here. The injected logger already owns the file: it rotates on
        // startup and appends through its own writer. Probing the path with File.Exists and then
        // opening a ReadWrite handle races that writer two ways:
        //   1. Startup rotation renames the file away, so File.Exists observes a window where the
        //      path is absent and this code takes the open branch.
        //   2. The logger appends with FileShare.Read, which a ReadWrite handle cannot share, so
        //      the open throws IOException and fails CLI startup.
        // Creating the file is the logger's job; the first LogInformation below proves the sink
        // works without touching the path ourselves.
        logger.LogInformation("Trackdub CLI started.");
    }
}
