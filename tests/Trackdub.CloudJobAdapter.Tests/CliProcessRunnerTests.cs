using System.Text.Json;
using Trackdub.CloudJobAdapter;
using Trackdub.Contracts.Jobs;

namespace Trackdub.CloudJobAdapter.Tests;

public sealed class CliProcessRunnerTests
{
    [Fact]
    public async Task ObstructedLogPathCannotStartAChild()
    {
        string root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Join(root, "cli.stderr.log"));
        string result = Path.Join(root, "result.json");
        try
        {
            var error = await Record.ExceptionAsync(() => new CliProcessRunner().RunAsync(new("dotnet",
                [typeof(ProcessFixtureMarker).Assembly.Location, "hang", result], root, result), TestContext.Current.CancellationToken));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.False(File.Exists(result + ".started"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void StalledRenewalCannotHoldExecutionPastTheSafetyWindow()
    {
        var clock = new ManualClock(); var lease = new ExecutionLeaseWatchdog(clock);
        using var lifetime = new CancellationTokenSource();
        var stalledRenewal = new TaskCompletionSource();
        clock.Ticks = TimeSpan.FromSeconds(59).Ticks; lease.Check(lifetime); Assert.False(lifetime.IsCancellationRequested);
        clock.Ticks = TimeSpan.FromSeconds(60).Ticks; lease.Check(lifetime); Assert.True(lifetime.IsCancellationRequested);
        Assert.False(stalledRenewal.Task.IsCompleted);
    }
    private sealed class ManualClock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    [Theory]
    [InlineData("normal", 0, true)]
    [InlineData("missing", 0, false)]
    [InlineData("crash", 139, false)]
    [InlineData("failed", 0, true)]
    public async Task ActualExitAndStructuredFileRemainIndependent(string mode, int expectedExit, bool hasOutcome)
    {
        string root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string result = Path.Join(root, "result.json");
            var evidence = await new CliProcessRunner().RunAsync(new("dotnet", [typeof(ProcessFixtureMarker).Assembly.Location, mode, result], root, result), TestContext.Current.CancellationToken);
            Assert.Equal(expectedExit, evidence.ExitCode); Assert.Equal(hasOutcome, evidence.Outcome is not null);
            if (mode == "failed") Assert.Equal("failed", evidence.Outcome!.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ShellMetacharactersRemainLiteralArguments()
    {
        string root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Join(root, "args.json");
            string[] payload = ["a b", "x; echo bad", "$(touch bad)", "\"quote\""];
            await new CliProcessRunner().RunAsync(new("dotnet", [typeof(ProcessFixtureMarker).Assembly.Location, "echo", path, .. payload], root, Path.Join(root, "absent.json")), TestContext.Current.CancellationToken);
            Assert.Equal(payload, JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)));
            Assert.False(File.Exists(Path.Join(root, "bad")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CancellationStopsAndReapsTheChild()
    {
        string root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            string result = Path.Join(root, "result.json");
            var running = new CliProcessRunner().RunAsync(new("dotnet", [typeof(ProcessFixtureMarker).Assembly.Location, "hang", result], root, result), cancel.Token);
            for (int i = 0; i < 100 && !File.Exists(result + ".started"); i++) await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(result + ".started")); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.False(File.Exists(result));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ArtifactTraversalIsRejected()
    {
        await Assert.ThrowsAsync<IOException>(() => JobAdapter.VerifyArtifactAsync(Path.Join(Path.GetTempPath(), "project"),
            new WorkerArtifact("../escape.mp4", "video", 1, new string('a', 64)), TestContext.Current.CancellationToken));
    }
}
