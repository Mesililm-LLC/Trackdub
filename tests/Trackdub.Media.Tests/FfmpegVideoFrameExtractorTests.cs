using Trackdub.Media.Extraction;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class FfmpegVideoFrameExtractorTests
{
    [Fact]
    public async Task ExtractTurnFramesAsync_writes_rgba_frame_sequence_with_image2_muxer()
    {
        var runner = new RecordingProcessRunner();
        string outputDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        var extractor = new FfmpegVideoFrameExtractor(runner, Environment.ProcessPath, Environment.ProcessPath);

        await extractor.ExtractTurnFramesAsync(
            "video.mp4",
            0,
            1,
            outputDirectory,
            CancellationToken.None);

        IReadOnlyList<string> extractionArgs = runner.Calls[1].Arguments;
        AssertOptionValue(extractionArgs, "-f", "image2");
        AssertOptionValue(extractionArgs, "-c:v", "rawvideo");
        AssertOptionValue(extractionArgs, "-pix_fmt", "rgba");
        Assert.Equal(Path.Join(outputDirectory, "frame_%06d.rgba"), extractionArgs[^1]);
    }

    [Fact]
    public async Task ExtractTurnFramesAsync_uses_fps_mode_because_ffmpeg_9_removed_vsync()
    {
        var runner = new RecordingProcessRunner();
        string outputDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        var extractor = new FfmpegVideoFrameExtractor(runner, Environment.ProcessPath, Environment.ProcessPath);

        await extractor.ExtractTurnFramesAsync("video.mp4", 0, 1, outputDirectory, CancellationToken.None);

        IReadOnlyList<string> extractionArgs = runner.Calls[1].Arguments;
        AssertOptionValue(extractionArgs, "-fps_mode", "passthrough");
        Assert.DoesNotContain("-vsync", extractionArgs);
    }

    [Fact]
    public async Task AssembleFramesAsync_reads_rgba_frame_sequence_with_image2_demuxer()
    {
        var runner = new RecordingProcessRunner();
        string framesDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        var extractor = new FfmpegVideoFrameExtractor(runner, Environment.ProcessPath);

        await extractor.AssembleFramesAsync(
            framesDirectory,
            Path.Join(framesDirectory, "out.mp4"),
            320,
            180,
            24,
            CancellationToken.None);

        IReadOnlyList<string> args = runner.Calls.Single().Arguments;
        AssertOptionValue(args, "-f", "image2");
        AssertOptionValue(args, "-framerate", "24.000000");
        AssertOptionValue(args, "-video_size", "320x180");
        AssertOptionValue(args, "-pix_fmt", "rgba");
        AssertOptionValue(args, "-c:v", "rawvideo");
        Assert.Contains(Path.Join(framesDirectory, "frame_%06d.rgba"), args);
    }

    private static void AssertOptionValue(IReadOnlyList<string> args, string option, string expectedValue)
    {
        int index = Enumerable.Range(0, args.Count).FirstOrDefault(i => args[i] == option, -1);
        Assert.True(index >= 0, $"Expected FFmpeg option '{option}'.");
        Assert.True(index + 1 < args.Count, $"Expected value for FFmpeg option '{option}'.");
        Assert.Equal(expectedValue, args[index + 1]);
    }

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessCall> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null)
        {
            Calls.Add(new ProcessCall(executablePath, arguments.ToArray()));
            string joined = string.Join(' ', arguments);
            if (joined.Contains("r_frame_rate", StringComparison.Ordinal))
            {
                return Task.FromResult(new ProcessResult(0, "24/1", string.Empty));
            }

            if (joined.Contains("stream=width,height", StringComparison.Ordinal))
            {
                return Task.FromResult(new ProcessResult(0, "320x180", string.Empty));
            }

            string? framePattern = arguments.LastOrDefault(arg => arg.EndsWith("frame_%06d.rgba", StringComparison.Ordinal));
            if (framePattern is not null)
            {
                string frameDirectory = Path.GetDirectoryName(framePattern)!;
                Directory.CreateDirectory(frameDirectory);
                File.WriteAllBytes(Path.Join(frameDirectory, "frame_000001.rgba"), [0, 0, 0, 255]);
            }

            return Task.FromResult(new ProcessResult(0, string.Empty, string.Empty));
        }
    }

    private sealed record ProcessCall(string ExecutablePath, IReadOnlyList<string> Arguments);
}
