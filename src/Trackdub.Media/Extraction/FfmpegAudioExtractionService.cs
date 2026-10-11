using Trackdub.Contracts;
using Trackdub.Media.Process;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Extraction;

public sealed class FfmpegAudioExtractionService : IAudioExtractionService
{
    private readonly IProcessRunner processRunner;
    private readonly FfmpegToolResolver toolResolver;

    public FfmpegAudioExtractionService(string? ffmpegPath = null)
        : this(new ProcessRunner(), ffmpegPath)
    {
    }

    internal FfmpegAudioExtractionService(IProcessRunner processRunner, string? ffmpegPath = null)
    {
        this.processRunner = processRunner;
        toolResolver = new FfmpegToolResolver(ffmpegPath);
    }
    public async Task<AudioExtractionResult> ExtractNormalizedAudioAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken,
        int? maxEncoderThreads = null)
    {
        AudioExtractionResult result = await ExtractInternalAsync(
            sourcePath,
            destinationPath,
            sampleRate: 48000,
            channelCount: 2,
            cancellationToken,
            maxEncoderThreads).ConfigureAwait(false);

        // Export mixes the whole normalized track in one float[] (WavePcm16.ReadSamplesAsync), so fail here,
        // before hours of pipeline work, rather than with an OverflowException at export.
        if (DescribeUnmixableLength(result) is string message)
        {
            File.Delete(result.OutputPath);
            throw new InvalidOperationException(message);
        }

        return result;
    }

    internal static string? DescribeUnmixableLength(AudioExtractionResult result)
    {
        long maxFrames = Array.MaxLength / result.ChannelCount;
        if (result.SampleFrames <= maxFrames)
        {
            return null;
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"Source audio is {result.DurationSeconds / 3600d:F2} h long; the longest supported source is " +
            $"{maxFrames / (double)result.SampleRate / 3600d:F2} h ({result.SampleRate} Hz, {result.ChannelCount} channels).");
    }

    public Task<AudioExtractionResult> ExtractStemSeparationAudioAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken) =>
        ExtractInternalAsync(sourcePath, destinationPath, sampleRate: 44100, channelCount: 2, cancellationToken, maxEncoderThreads: null);

    private async Task<AudioExtractionResult> ExtractInternalAsync(
        string sourcePath,
        string destinationPath,
        int sampleRate,
        int channelCount,
        CancellationToken cancellationToken,
        int? maxEncoderThreads)
    {
        string fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath))
        {
            throw new FileNotFoundException("Source media file was not found.", fullSourcePath);
        }

        string fullDestinationPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestinationPath)!);
        if (File.Exists(fullDestinationPath))
        {
            File.Delete(fullDestinationPath);
        }

        List<string> arguments =
        [
            "-y",
            "-hide_banner",
            "-loglevel", "error",
            "-i", fullSourcePath,
            "-vn",
            "-sn",
            "-dn",
            "-map", "0:a:0",
            "-ac", channelCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:a", "pcm_s16le",
            // ffmpeg's default (-rf64 never) leaves the 0xFFFFFFFF size placeholder in WAVs past 4 GiB
            // (~6.2 h at 48 kHz stereo) and still exits 0; auto upgrades those to RF64, which WavePcm16 reads.
            "-rf64", "auto",
        ];
        if (maxEncoderThreads is int threadBudget && threadBudget > 0)
        {
            arguments.Add("-threads");
            arguments.Add(threadBudget.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        arguments.Add(fullDestinationPath);

        ProcessResult result = await processRunner.RunAsync(
            toolResolver.ResolveFfmpegPath(),
            arguments,
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"ffmpeg audio extraction failed with exit code {result.ExitCode}: {result.StandardError}".Trim());
        }

        if (!File.Exists(fullDestinationPath))
        {
            throw new InvalidOperationException("ffmpeg completed without producing an audio file.");
        }

        WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(fullDestinationPath, cancellationToken).ConfigureAwait(false);
        return new AudioExtractionResult(
            fullDestinationPath,
            waveInfo.DurationSeconds,
            waveInfo.SampleRate,
            waveInfo.ChannelCount,
            waveInfo.SampleFrames);
    }
}
