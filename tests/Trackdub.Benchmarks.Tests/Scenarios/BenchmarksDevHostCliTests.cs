using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;

namespace Trackdub.Benchmarks.Tests.Scenarios;

[Trait("Category", "Slow")]
public sealed class BenchmarksDevHostCliTests : IDisposable
{
    private readonly MockDubbingBenchmarkHarness _harness = new();
    private readonly string _tempOutputDir;

    public BenchmarksDevHostCliTests()
    {
        _tempOutputDir = Path.Join(Path.GetTempPath(), $"trackdub_cli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_tempOutputDir))
        {
            try { Directory.Delete(_tempOutputDir, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
        }
    }

    [Fact]
    public async Task Cli_ControlledRun_AcceptsMockFlag_ExecutesSuccessfully()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled", fixture, "--output", _tempOutputDir, "--mock"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("Evidence", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_ControlledRun_ReportsHostCapacityBeforeTheRunStarts()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled", fixture, "--output", _tempOutputDir, "--mock"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains("Host capacity", text, StringComparison.Ordinal);
        Assert.Contains("Effective VRAM capacity", text, StringComparison.Ordinal);
        Assert.True(
            text.IndexOf("Host capacity", StringComparison.Ordinal)
                < text.IndexOf("Evidence", StringComparison.Ordinal),
            "the host-capacity banner must be printed before the run's outcome");
    }

    [Fact]
    public async Task Cli_ControlledRun_AcceptsDryRunFlag_SynonymForMock()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled", fixture, "--output", _tempOutputDir, "--dry-run"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Cli_ControlledMatrix_AcceptsMockFlag()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled-matrix", fixture, "--output", _tempOutputDir, "--mock"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string text = output.ToString();
        Assert.Contains("Host capacity", text, StringComparison.Ordinal);
        int bannerCount = (text.Length - text.Replace("Host capacity", string.Empty, StringComparison.Ordinal).Length)
            / "Host capacity".Length;
        Assert.Equal(1, bannerCount);
        Assert.True(
            text.IndexOf("Host capacity", StringComparison.Ordinal)
                < text.IndexOf("Stage matrix", StringComparison.Ordinal),
            "the host-capacity banner must be printed once, before the first stage runs");
    }

    [Fact]
    public async Task Cli_MatrixCommand_ValidatesRequiredOutputDirectory()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled-matrix", fixture],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("--output", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_MatrixCommand_HelpOption_ReturnsZero()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["controlled-matrix", "--help"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("controlled-matrix", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_MatrixCommand_RejectsUnknownProvider()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", _tempOutputDir, "--mock", "--providers", "cuda,invalid_ep"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Contains("invalid_ep", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cli_ReportFormatOptions_SupportsJsonConsoleBoth()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);

        foreach (string format in new[] { "json", "console", "both" })
        {
            string outDir = Path.Join(_tempOutputDir, format);
            Directory.CreateDirectory(outDir);
            using var output = new StringWriter();
            using var error = new StringWriter();

            int exitCode = await Program.RunAsync(
                ["matrix", fixture, "--output", outDir, "--mock", "--providers", "cpu,directml", "--format", format],
                TextReader.Null,
                output,
                error,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            if (format is "console" or "both")
            {
                Assert.Contains("Execution Provider Matrix", output.ToString(), StringComparison.OrdinalIgnoreCase);
                // A mock comparison row names the simulated latency budget it was built from, so the
                // console summary cannot be mistaken for measured provider performance.
                Assert.Contains("SimulatedBudget=", output.ToString(), StringComparison.Ordinal);
            }
            if (format is "json" or "both")
            {
                Assert.True(File.Exists(Path.Join(outDir, "execution-provider-matrix.json")),
                    $"Expected execution-provider-matrix.json in {outDir}");
            }
        }
    }

    [Fact]
    public async Task Cli_Cancellation_ReturnsNonZeroOrPropagatesException()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            int exitCode = await Program.RunAsync(
                ["controlled", fixture, "--output", _tempOutputDir, "--mock"],
                TextReader.Null,
                output,
                error,
                cts.Token);

            Assert.NotEqual(0, exitCode);
        }
        catch (OperationCanceledException)
        {
            // Propagating cancellation exception is also valid behavior
            Assert.True(true);
        }
    }
}
