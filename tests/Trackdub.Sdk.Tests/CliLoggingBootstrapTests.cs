using Trackdub.Cli;

namespace Trackdub.Sdk.Tests;

public sealed class CliLoggingBootstrapTests : IDisposable
{
    private readonly string root = Path.Join(Path.GetTempPath(), "Trackdub.Sdk.Tests", "log-" + Guid.NewGuid().ToString("N"));

    public CliLoggingBootstrapTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnsureLogFileExists_creates_a_missing_log_file()
    {
        string logPath = Path.Join(root, "trackdub.log");

        CliLoggingBootstrap.EnsureLogFileExists(logPath);

        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void EnsureLogFileExists_leaves_a_log_another_session_holds_exclusively_alone()
    {
        string logPath = Path.Join(root, "trackdub.log");
        using FileStream holder = new(logPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        CliLoggingBootstrap.EnsureLogFileExists(logPath);

        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void EnsureLogFileExists_still_reports_a_log_that_cannot_be_created()
    {
        string logPath = Path.Join(root, "missing-directory", "trackdub.log");

        Assert.ThrowsAny<IOException>(() => CliLoggingBootstrap.EnsureLogFileExists(logPath));
    }
}
