using Trackdub.TestDoubles;
using Trackdub.Tools;

namespace Trackdub.Benchmarks.Tests;

public sealed class StemLabCommandOptionsTests
{
    [Fact]
    public void TryParse_ExplicitSeparatorCommandParsesExpectedValues()
    {
        string mediaPath = Path.Join("fixtures", "sample.mp4");
        string outputPath = Path.Join("artifacts", "stem-lab");
        string modelPath = Path.Join("models", "stem-sep", "stem_separator.onnx");

        bool success = StemLabCommandOptions.TryParse(
            [
                "--media", mediaPath,
                "--output", outputPath,
                "--model", modelPath,
                "--separator-exe", "python.exe",
                "--separator-arg", "inference.py",
                "--separator-arg", "--model_path",
                "--separator-arg", "{model}",
                "--separator-arg", "--input",
                "--separator-arg", "{input}",
                "--separator-arg", "--output_folder",
                "--separator-arg", "{separatorOutput}",
                "--ffmpeg", "ffmpeg.exe",
                "--keep-work",
                "--timeout-seconds", "120"
            ],
            TextWriter.Null,
            out StemLabCommandOptions options);

        Assert.True(success);
        Assert.False(options.ShowHelp);
        Assert.Equal(Path.GetFullPath(mediaPath), options.SourceMediaPath);
        Assert.Equal(Path.GetFullPath(outputPath), options.OutputDirectory);
        Assert.Equal(Path.GetFullPath(modelPath), options.ModelPath);
        Assert.EndsWith("python.exe", options.SeparatorExecutablePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{input}", options.SeparatorArguments);
        Assert.Contains("{model}", options.SeparatorArguments);
        Assert.Contains("{separatorOutput}", options.SeparatorArguments);
        Assert.EndsWith("ffmpeg.exe", options.FfmpegPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(options.KeepWorkDirectory);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Timeout);
    }

    [Fact]
    public void TryParse_RejectsCommandWithoutInputToken()
    {
        bool success = StemLabCommandOptions.TryParse(
            [
                "--media", "sample.mp4",
                "--output", "stem-lab",
                "--model", "stem_separator.onnx",
                "--separator-exe", "python.exe",
                "--separator-arg", "inference.py",
                "--separator-arg", "--model_path",
                "--separator-arg", "{model}",
                "--separator-arg", "--output_folder",
                "--separator-arg", "{separatorOutput}"
            ],
            TextWriter.Null,
            out _);

        Assert.False(success);
    }

    [Fact]
    public void TryParse_AcceptsInputFolderTokenForReferencePipelines()
    {
        bool success = StemLabCommandOptions.TryParse(
            [
                "--media", "sample.mp4",
                "--output", "stem-lab",
                "--model", "stem_separator.onnx",
                "--separator-exe", "python.exe",
                "--separator-arg", "inference.py",
                "--separator-arg", "--model_path",
                "--separator-arg", "{model}",
                "--separator-arg", "--input_folder",
                "--separator-arg", "{inputFolder}",
                "--separator-arg", "--output_folder",
                "--separator-arg", "{separatorOutput}"
            ],
            TextWriter.Null,
            out StemLabCommandOptions options);

        Assert.True(success);
        Assert.Contains("{inputFolder}", options.SeparatorArguments);
    }
}

public sealed class StemLabCommandTests
{
    [Fact]
    public async Task RunAsync_PrintsStemSummaryAndWarnings()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var runner = new FakeStemLabCommandRunner();

        int exitCode = await StemLabCommand.RunAsync(
            [
                "--media", "sample.mp4",
                "--output", "stem-lab",
                "--model", "stem_separator.onnx",
                "--separator-exe", "python.exe",
                "--separator-arg", "inference.py",
                "--separator-arg", "--model_path",
                "--separator-arg", "{model}",
                "--separator-arg", "--input",
                "--separator-arg", "{input}",
                "--separator-arg", "--output_folder",
                "--separator-arg", "{separatorOutput}"
            ],
            output,
            error,
            runner,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("StemLab complete", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Vocals:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Instrumental:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Diagnostics:", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Warnings:", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath("sample.mp4"), runner.LastOptions!.SourceMediaPath);
    }

    [Fact]
    public async Task ProgramRunAsync_StemLabHelpDispatchesToStemLabUsage()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        int exitCode = await Trackdub.Tools.Program.RunAsync(["stem-lab", "--help"], output, error, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("Trackdub.Tools stem-lab", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Trackdub.Tools ingest", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class FakeStemLabCommandRunner : IStemLabCommandRunner
    {
        public StemLabCommandOptions? LastOptions { get; private set; }

        public Task<StemLabCommandResult> RunAsync(StemLabCommandOptions options, CancellationToken cancellationToken)
        {
            LastOptions = options;
            var source = new StemLabAudioMetrics("source", 44100, 2, 44100, 1.0, -1.0, -18.0, 0.0, 0.05);
            var vocals = new StemLabAudioMetrics("vocals", 44100, 2, 44100, 1.0, -3.0, -21.0, 0.0, 0.08);
            var instrumental = new StemLabAudioMetrics("instrumental", 44100, 2, 44100, 1.0, -4.0, -19.0, 0.0, 0.07);
            var reconstruction = new StemLabReconstructionMetrics(1.0, -36.0, 0.01);
            StemLabDiagnostics diagnostics = new(source, vocals, instrumental, reconstruction, ["High-frequency energy warning."]);
            StemLabCommandResult result = new(
                Path.GetFullPath(Path.Join("stem-lab", "_work", "stem-source.wav")),
                Path.GetFullPath(Path.Join("stem-lab", "vocals.wav")),
                Path.GetFullPath(Path.Join("stem-lab", "instrumental.wav")),
                Path.GetFullPath(Path.Join("stem-lab", "diagnostics.json")),
                diagnostics);
            return Task.FromResult(result);
        }
    }
}

public sealed class StemLabWaveAnalyzerHeaderTests
{
    [Fact]
    public void ReadHeader_reads_rf64_data_length_from_ds64()
    {
        const long dataLength = 5_000_000_000L;
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: true, dataChunkSize: uint.MaxValue, ds64DataLength: dataLength, dataLength);

        StemLabWaveAnalyzer.WavePcm16Header header = StemLabWaveAnalyzer.ReadHeader(stream, "stem-source.wav");

        Assert.Equal(SparseWaveStream.Rf64DataStart, header.DataOffset);
        Assert.Equal(dataLength, header.DataByteLength);
        Assert.Equal(dataLength / 4, header.SampleFrames);
    }

    [Fact]
    public void ReadHeader_reads_riff_data_chunk_larger_than_int_max()
    {
        const uint dataLength = 3_000_000_000u;
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: false, dataChunkSize: dataLength, ds64DataLength: null, dataLength);

        StemLabWaveAnalyzer.WavePcm16Header header = StemLabWaveAnalyzer.ReadHeader(stream, "stem-source.wav");

        Assert.Equal(dataLength, header.DataByteLength);
    }

    [Fact]
    public void ReadHeader_rejects_riff_data_chunk_size_placeholder()
    {
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: false, dataChunkSize: uint.MaxValue, ds64DataLength: null, dataLength: 5_000_000_000L);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StemLabWaveAnalyzer.ReadHeader(stream, "stem-source.wav"));

        Assert.Contains("0xFFFFFFFF", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadHeader_rejects_rf64_without_ds64()
    {
        using SparseWaveStream stream = SparseWaveStream.CreatePcm16Stereo48k(rf64: true, dataChunkSize: uint.MaxValue, ds64DataLength: null, dataLength: 5_000_000_000L);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => StemLabWaveAnalyzer.ReadHeader(stream, "stem-source.wav"));

        Assert.Contains("ds64", exception.Message, StringComparison.Ordinal);
    }
}
