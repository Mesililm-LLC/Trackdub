using System.Globalization;
using Trackdub.Contracts.Pipeline;
using Trackdub.Media.Process;

namespace Trackdub.Media.Extraction;

public sealed class FfmpegVideoFrameExtractor : IVideoFrameExtractor, IVideoFrameAssembler
{
    private readonly IProcessRunner _processRunner;
    private readonly FfmpegToolResolver _toolResolver;

    public FfmpegVideoFrameExtractor(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegVideoFrameExtractor(IProcessRunner processRunner, string? ffmpegPath = null, string? ffprobePath = null)
    {
        _processRunner = processRunner;
        _toolResolver = new FfmpegToolResolver(ffmpegPath, ffprobePath);
    }

    public async Task<FrameExtractionResult> ExtractTurnFramesAsync(
        string videoPath,
        double startSeconds,
        double endSeconds,
        string outputDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        Directory.CreateDirectory(outputDirectory);
        foreach (string stale in Directory.GetFiles(outputDirectory, "frame_*.rgba"))
            File.Delete(stale);
        string ffmpegPath = _toolResolver.ResolveFfmpegPath();

        // First pass: probe the frame rate.
        double frameRate = await ProbeFrameRateAsync(ffmpegPath, videoPath, cancellationToken)
            .ConfigureAwait(false);

        string framePattern = Path.Join(outputDirectory, "frame_%06d.rgba");

        // Extract raw RGBA frames using image2 muxer + rawvideo codec.
        // image2 writes one file per frame; rawvideo emits raw pixel data with no container.
        // -fps_mode passthrough keeps every frame without duplication or dropping (FFmpeg 9 removed -vsync)
        var args = new List<string>
        {
            "-ss", startSeconds.ToString("F6", CultureInfo.InvariantCulture),
            "-to", endSeconds.ToString("F6", CultureInfo.InvariantCulture),
            "-i", videoPath,
            "-fps_mode", "passthrough",
            "-f", "image2",
            "-c:v", "rawvideo",
            "-pix_fmt", "rgba",
            "-y",
            framePattern
        };

        ProcessResult result = await _processRunner.RunAsync(ffmpegPath, args, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg frame extraction failed (exit {result.ExitCode}): {result.StandardError}");
        }

        string[] files = Directory.GetFiles(outputDirectory, "frame_*.rgba");
        Array.Sort(files, StringComparer.Ordinal);

        // Probe the actual output dimensions from the video stream.
        int frameWidth = 0;
        int frameHeight = 0;
        if (files.Length > 0)
        {
            (frameWidth, frameHeight) = await ProbeVideoDimensionsAsync(ffmpegPath, videoPath, cancellationToken)
                .ConfigureAwait(false);
            if (frameWidth == 0 || frameHeight == 0)
                throw new InvalidOperationException(
                    $"FFmpeg dimension probe returned (0,0) for '{videoPath}'. Cannot process extracted frames.");
        }

        return new FrameExtractionResult(outputDirectory, frameWidth, frameHeight, files.Length, frameRate);
    }

    public async Task AssembleFramesAsync(
        string framesDirectory,
        string outputVideoPath,
        int width,
        int height,
        double frameRate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(framesDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputVideoPath);

        string? outDir = Path.GetDirectoryName(outputVideoPath);
        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
        }

        string ffmpegPath = _toolResolver.ResolveFfmpegPath();
        string framePattern = Path.Join(framesDirectory, "frame_%06d.rgba");

        // image2 demuxer reads the numbered file sequence; rawvideo decoder reads raw RGBA pixel data.
        // Dimension and pixel format are required for rawvideo to interpret each file correctly.
        // -start_number 1: image2 muxer starts frames at 000001; demuxer defaults to 0 so must match.
        var args = new List<string>
        {
            "-f", "image2",
            "-framerate", frameRate.ToString("F6", CultureInfo.InvariantCulture),
            "-video_size", $"{width}x{height}",
            "-pix_fmt", "rgba",
            "-c:v", "rawvideo",
            "-start_number", "1",
            "-i", framePattern,
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-y",
            outputVideoPath
        };

        ProcessResult result = await _processRunner.RunAsync(ffmpegPath, args, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"FFmpeg frame assembly failed (exit {result.ExitCode}): {result.StandardError}");
        }
    }

    private async Task<double> ProbeFrameRateAsync(
        string ffmpegPath, string videoPath, CancellationToken cancellationToken)
    {
        string ffprobePath = _toolResolver.ResolveFfprobePath();
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=r_frame_rate",
            "-of", "default=noprint_wrappers=1:nokey=1",
            videoPath
        };

        ProcessResult result = await _processRunner.RunAsync(ffprobePath, args, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            string rateStr = result.StandardOutput.Trim();
            // Rate may be a fraction like "25/1" or "30000/1001"
            string[] parts = rateStr.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double num) &&
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double den) &&
                den > 0)
            {
                return num / den;
            }
        }

        return 25.0; // safe fallback
    }

    private async Task<(int Width, int Height)> ProbeVideoDimensionsAsync(
        string ffmpegPath, string videoPath, CancellationToken cancellationToken)
    {
        string ffprobePath = _toolResolver.ResolveFfprobePath();
        var args = new List<string>
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-show_entries", "stream=width,height",
            "-of", "csv=s=x:p=0",
            videoPath
        };

        ProcessResult result = await _processRunner.RunAsync(ffprobePath, args, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            string[] parts = result.StandardOutput.Trim().Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out int w) &&
                int.TryParse(parts[1], out int h))
            {
                return (w, h);
            }
        }

        return (0, 0);
    }
}
