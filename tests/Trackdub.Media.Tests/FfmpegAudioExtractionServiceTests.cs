using Trackdub.Media.Extraction;
using Trackdub.Media.Process;
using Trackdub.Media.Waveforms;

namespace Trackdub.Media.Tests;

public sealed class FfmpegAudioExtractionServiceTests
{
    [RequiresFfmpegFact]
    public async Task ExtractNormalizedAudioAsync_creates_stereo_wav_output_and_waveform()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            string sourcePath = await MediaFixtureFactory.CreateSampleVideoAsync(tempDirectory);
            string outputPath = Path.Join(tempDirectory, "normalized.wav");
            var service = new FfmpegAudioExtractionService();
            var waveformGenerator = new WaveformSummaryGenerator(bucketCount: 16);

            var result = await service.ExtractNormalizedAudioAsync(sourcePath, outputPath, TestContext.Current.CancellationToken);
            var waveform = await waveformGenerator.GenerateAsync(outputPath, TestContext.Current.CancellationToken);

            WavePcm16Info waveInfo = await WavePcm16.ReadInfoAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(outputPath, result.OutputPath);
            Assert.Equal(48000, result.SampleRate);
            Assert.Equal(2, result.ChannelCount);
            Assert.True(result.DurationSeconds > 1d);
            Assert.Equal(48000, waveInfo.SampleRate);
            Assert.Equal(2, waveInfo.ChannelCount);
            Assert.Equal(16, waveform.BucketCount);
            Assert.Equal(48000, waveform.SampleRate);
            Assert.Equal(2, waveform.ChannelCount);
            Assert.Contains(waveform.Peaks, static peak => peak > 0f);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ExtractNormalizedAudioAsync_requests_rf64_for_outputs_beyond_4_gib()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempDirectory);
            string sourcePath = Path.Join(tempDirectory, "source.mp4");
            await File.WriteAllBytesAsync(sourcePath, [0], TestContext.Current.CancellationToken);
            string outputPath = Path.Join(tempDirectory, "normalized.wav");
            var runner = new WavWritingProcessRunner();
            var service = new FfmpegAudioExtractionService(runner, Environment.ProcessPath);

            await service.ExtractNormalizedAudioAsync(sourcePath, outputPath, TestContext.Current.CancellationToken);

            IReadOnlyList<string> arguments = Assert.Single(runner.Calls);
            int rf64Index = arguments.ToList().IndexOf("-rf64");
            Assert.True(rf64Index >= 0, "ffmpeg extraction must pass -rf64.");
            Assert.Equal("auto", arguments[rf64Index + 1]);
            Assert.Equal(outputPath, arguments[^1]);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private sealed class WavWritingProcessRunner : IProcessRunner
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public async Task<ProcessResult> RunAsync(
            string executablePath,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken,
            ProcessRunOptions? options = null)
        {
            Calls.Add(arguments.ToArray());
            await WavePcm16.WriteSamplesAsync(arguments[^1], [0f, 0f], sampleRate: 48000, channelCount: 2, cancellationToken);
            return new ProcessResult(0, string.Empty, string.Empty);
        }
    }
}
