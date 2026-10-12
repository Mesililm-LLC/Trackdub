using System.Buffers.Binary;
using System.Text;
using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Mixing;
using Trackdub.Media.Mixing;
using Trackdub.Media.Waveforms;
using Trackdub.TestDoubles;

namespace Trackdub.Media.Tests;

public sealed class PreviewRangeRendererTests
{
    [Theory]
    [InlineData(0.5d)]
    [InlineData(1.25d)]
    public async Task RenderAsync_mixes_placed_stereo_media_at_offset_and_trims_to_slot(double rangeStart)
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Join(root, "source.wav");
            string media = Path.Join(root, "media.wav");
            string output = Path.Join(root, "output.wav");
            WriteStereoConstantWave(source, 1000, 4d, 0.1f, 0.1f);
            WriteStereoConstantWave(media, 500, 2d, 0.25f, 0.05f);
            var store = new FakeArtifactStore();
            store.SeedPath("source.wav", source);
            store.SeedPath("media.wav", media);
            var plan = new MixPlan(Guid.NewGuid(), null, ArtifactKind.NormalizedAudio, "source.wav",
                0d, -96d, 0d, 0d, 0d, DateTimeOffset.UtcNow, [], [], [], OutputChannelCount: 2,
                MediaClips: [new MixMediaClip("media.wav", 1d, 0.5d)]);
            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, rangeStart, 2.5d, output), TestContext.Current.CancellationToken);
            var samples = await WavePcm16.ReadAllSamplesAsync(output, TestContext.Current.CancellationToken);
            int within = (int)((Math.Max(1d, rangeStart) + 0.1d - rangeStart) * 1000d) * 2;
            Assert.InRange(samples.Samples[within], 0.34f, 0.36f);
            Assert.InRange(samples.Samples[within + 1], 0.14f, 0.16f);
            int after = (int)((1.8d - rangeStart) * 1000d) * 2;
            Assert.InRange(samples.Samples[after], 0.09f, 0.11f);
            Assert.InRange(samples.Samples[after + 1], 0.09f, 0.11f);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RenderAsync_fails_for_missing_placed_audio()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Join(root, "source.wav");
            WriteConstantWave(source, 1000, 4d, 0.1f);
            var store = new FakeArtifactStore();
            store.SeedPath("source.wav", source);
            store.SeedPath("missing.wav", Path.Join(root, "missing.wav"));
            var plan = new MixPlan(Guid.NewGuid(), null, ArtifactKind.NormalizedAudio, "source.wav",
                0d, 0d, 0d, 0d, 0d, DateTimeOffset.UtcNow, [], [], [],
                MediaClips: [new MixMediaClip("missing.wav", 1d, 0.5d)]);
            await Assert.ThrowsAsync<FileNotFoundException>(() => new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, 0d, 3d, Path.Join(root, "output.wav")), TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task RenderAsync_outputs_requested_duration_and_places_take_at_segment_offset()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 4.0d, amplitude: 0.5f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.25f);

            Guid projectId = Guid.NewGuid();
            Guid segmentId = Guid.NewGuid();
            Guid takeId = Guid.NewGuid();
            Guid artifactId = Guid.NewGuid();
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                projectId,
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: -12d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, segmentId, 1.0d, 2.0d, takeId, artifactId, "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions:
                [
                    new MixDuckRegion(0, segmentId, 0.95d, 2.18d, -12d)
                ],
                Warnings: []);
            var renderer = new PreviewRangeRenderer(store);

            PreviewRangeRenderResult result = await renderer.RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0.5d, EndSeconds: 2.5d, outputPath),
                TestContext.Current.CancellationToken);

            float[] outputSamples = ReadMonoSamples(outputPath, out int sampleRate);
            Assert.Equal(1000, sampleRate);
            Assert.Equal(2.0d, result.DurationSeconds, precision: 3);
            Assert.InRange(Math.Abs((outputSamples.Length / (double)sampleRate) - 2.0d), 0d, 0.05d);
            Assert.InRange(outputSamples[250], 0.49f, 0.51f);
            Assert.InRange(outputSamples[600], 0.32f, 0.36f);
            Assert.InRange(outputSamples[1800], 0.49f, 0.51f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_resamples_long_lower_rate_takes_without_collapsing_to_one_sample()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 44100, durationSeconds: 4.0d, amplitude: 0.05f);
            WriteConstantWave(takePath, sampleRate: 24000, durationSeconds: 3.0d, amplitude: 0.20f);

            Guid projectId = Guid.NewGuid();
            Guid segmentId = Guid.NewGuid();
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                projectId,
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(
                        0,
                        segmentId,
                        StartSeconds: 0.5d,
                        EndSeconds: 3.5d,
                        TakeId: Guid.NewGuid(),
                        ArtifactId: Guid.NewGuid(),
                        TakeRelativePath: "artifacts/tts/take.wav",
                        TakeDurationSeconds: 3.0d,
                        IsSilentGap: false,
                        WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: []);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 4d, outputPath),
                TestContext.Current.CancellationToken);

            float[] outputSamples = ReadMonoSamples(outputPath, out int sampleRate);
            int sampleInsideDub = (int)Math.Round(2.0d * sampleRate);
            int sampleAfterDub = (int)Math.Round(3.75d * sampleRate);

            Assert.InRange(outputSamples[sampleInsideDub], 0.23f, 0.27f);
            Assert.InRange(outputSamples[sampleAfterDub], 0.04f, 0.06f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_preserves_stereo_source_and_centers_mono_take_when_pan_restore_is_off()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0.20f, rightAmplitude: -0.10f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.0d, Guid.NewGuid(), Guid.NewGuid(), "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: [],
                OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
                OutputChannelCount: 2,
                RestoreOriginalPan: false);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1.5d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.Equal(2, result.ChannelCount);
            Assert.Equal(2, output.ChannelCount);
            float sourceLeft = output.SampleAt(frame: 100, channel: 0);
            float sourceRight = output.SampleAt(frame: 100, channel: 1);
            Assert.InRange(sourceLeft, 0.19f, 0.21f);
            Assert.InRange(sourceRight, -0.11f, -0.09f);

            float leftDubContribution = output.SampleAt(frame: 650, channel: 0) - sourceLeft;
            float rightDubContribution = output.SampleAt(frame: 650, channel: 1) - sourceRight;
            Assert.InRange(leftDubContribution, 0.24f, 0.31f);
            Assert.InRange(rightDubContribution, 0.24f, 0.31f);
            Assert.InRange(Math.Abs(leftDubContribution - rightDubContribution), 0f, 0.02f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_restores_original_segment_pan_for_mono_take_when_enabled()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "ambiance.wav");
            string originalPath = Path.Join(tempRoot, "normalized.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);
            WriteStereoConstantWave(originalPath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0.05f, rightAmplitude: 0.45f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath("artifacts/stems/ambiance.wav", sourcePath);
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, originalPath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.Ambiance,
                "artifacts/stems/ambiance.wav",
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.0d, Guid.NewGuid(), Guid.NewGuid(), "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: [],
                OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
                OutputChannelCount: 2,
                RestoreOriginalPan: true);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1.5d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            float left = output.SampleAt(frame: 650, channel: 0);
            float right = output.SampleAt(frame: 650, channel: 1);
            Assert.True(right > left + 0.15f, $"Expected right-heavy panned TTS, got L={left:0.000}, R={right:0.000}.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_uses_take_duration_for_original_pan_analysis_window()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "ambiance.wav");
            string originalPath = Path.Join(tempRoot, "normalized.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);
            var originalSamples = new float[2000 * 2];
            for (int frame = 500; frame < 700; frame++)
            {
                int offset = frame * 2;
                originalSamples[offset] = 0.05f;
                originalSamples[offset + 1] = 0.45f;
            }

            for (int frame = 700; frame < 1500; frame++)
            {
                int offset = frame * 2;
                originalSamples[offset] = 0.45f;
                originalSamples[offset + 1] = 0.05f;
            }

            WriteWave(originalPath, originalSamples, sampleRate: 1000, channelCount: 2);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.2d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath("artifacts/stems/ambiance.wav", sourcePath);
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, originalPath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.Ambiance,
                "artifacts/stems/ambiance.wav",
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.5d, Guid.NewGuid(), Guid.NewGuid(), "artifacts/tts/take.wav", 0.2d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: [],
                OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
                OutputChannelCount: 2,
                RestoreOriginalPan: true);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            float left = output.SampleAt(frame: 600, channel: 0);
            float right = output.SampleAt(frame: 600, channel: 1);
            Assert.True(right > left + 0.15f, $"Expected take-duration pan analysis to stay right-heavy, got L={left:0.000}, R={right:0.000}.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_restores_pan_from_multichannel_original_mix_downmix()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "ambiance.wav");
            string originalPath = Path.Join(tempRoot, "normalized-5-1.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);

            var originalSamples = new float[2000 * 6];
            for (int frame = 0; frame < 2000; frame++)
            {
                int offset = frame * 6;
                originalSamples[offset + 0] = 0.05f; // incidental left bed
                originalSamples[offset + 2] = 0.90f; // centered dialogue
            }

            WriteWave(originalPath, originalSamples, sampleRate: 1000, channelCount: 6);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath("artifacts/stems/ambiance.wav", sourcePath);
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, originalPath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.Ambiance,
                "artifacts/stems/ambiance.wav",
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.0d, Guid.NewGuid(), Guid.NewGuid(), "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: [],
                OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
                OutputChannelCount: 2,
                RestoreOriginalPan: true);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1.5d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            float left = output.SampleAt(frame: 650, channel: 0);
            float right = output.SampleAt(frame: 650, channel: 1);
            Assert.InRange(left, 0.27f, 0.31f);
            Assert.InRange(right, 0.25f, 0.29f);
            Assert.InRange(Math.Abs(left - right), 0f, 0.05f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_centers_mono_take_when_original_mix_reference_is_mono()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "ambiance.wav");
            string originalPath = Path.Join(tempRoot, "normalized-mono.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);
            WriteConstantWave(originalPath, sampleRate: 1000, durationSeconds: 2.0d, amplitude: 0.40f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath("artifacts/stems/ambiance.wav", sourcePath);
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, originalPath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            MixPlan plan = CreateSingleClipPanRestorePlan(
                sourceAudioRelativePath: "artifacts/stems/ambiance.wav",
                takeRelativePath: "artifacts/tts/take.wav");

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1.5d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            float left = output.SampleAt(frame: 650, channel: 0);
            float right = output.SampleAt(frame: 650, channel: 1);
            Assert.InRange(left, 0.27f, 0.30f);
            Assert.InRange(right, 0.27f, 0.30f);
            Assert.InRange(Math.Abs(left - right), 0f, 0.02f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_centers_mono_take_when_original_mix_reference_is_silent()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "ambiance.wav");
            string originalPath = Path.Join(tempRoot, "normalized-silent.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);
            WriteStereoConstantWave(originalPath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath("artifacts/stems/ambiance.wav", sourcePath);
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, originalPath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            MixPlan plan = CreateSingleClipPanRestorePlan(
                sourceAudioRelativePath: "artifacts/stems/ambiance.wav",
                takeRelativePath: "artifacts/tts/take.wav");

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1.5d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            float left = output.SampleAt(frame: 650, channel: 0);
            float right = output.SampleAt(frame: 650, channel: 1);
            Assert.InRange(left, 0.27f, 0.30f);
            Assert.InRange(right, 0.27f, 0.30f);
            Assert.InRange(Math.Abs(left - right), 0f, 0.02f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_centers_mono_take_when_pan_reference_is_unusable()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            string missingOriginalPath = Path.Join(tempRoot, "missing-normalized.wav");
            WriteStereoConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, leftAmplitude: 0f, rightAmplitude: 0f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.40f);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, missingOriginalPath);
            store.SeedPath("media/source.wav", sourcePath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                "media/source.wav",
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.0d, Guid.NewGuid(), Guid.NewGuid(), "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: [],
                OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
                OutputChannelCount: 2,
                RestoreOriginalPan: true);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1.5d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.InRange(Math.Abs(output.SampleAt(frame: 650, channel: 0) - output.SampleAt(frame: 650, channel: 1)), 0f, 0.02f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_upmixes_mono_source_lane_when_original_mix_requests_stereo_output()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 1.0d, amplitude: 0.25f);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: [],
                OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
                OutputChannelCount: 2,
                RestoreOriginalPan: false);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.Equal(2, result.ChannelCount);
            Assert.Equal(2, output.ChannelCount);
            Assert.InRange(output.SampleAt(frame: 100, channel: 0), 0.24f, 0.26f);
            Assert.InRange(output.SampleAt(frame: 100, channel: 1), 0.24f, 0.26f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(4, 0.27f, 0.30f, 0.06f, 0.08f)]
    [InlineData(5, 0.34f, 0.37f, 0.41f, 0.44f)]
    public async Task RenderAsync_downmixes_four_and_five_channel_source_lanes_to_stereo_output(
        int channelCount,
        float minLeft,
        float maxLeft,
        float minRight,
        float maxRight)
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, $"source-{channelCount}.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            var samples = new float[1000 * channelCount];
            for (int frame = 0; frame < 1000; frame++)
            {
                int offset = frame * channelCount;
                // 4-channel uses quad FL,FR,BL,BR; 5-channel uses FL,FR,C,BL,BR.
                samples[offset + 2] = 0.40f;
                samples[offset + 3] = 0.10f;
                if (channelCount == 5)
                {
                    samples[offset + 4] = 0.20f;
                }
            }

            WriteWave(sourcePath, samples, sampleRate: 1000, channelCount: channelCount);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.Equal(2, result.ChannelCount);
            Assert.Equal(2, output.ChannelCount);
            Assert.InRange(output.SampleAt(frame: 100, channel: 0), minLeft, maxLeft);
            Assert.InRange(output.SampleAt(frame: 100, channel: 1), minRight, maxRight);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_downmixes_wave_extensible_five_one_source_lane_without_lfe_to_stereo_output()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source-5-1.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            var samples = new float[1000 * 6];
            for (int frame = 0; frame < 1000; frame++)
            {
                int offset = frame * 6;
                samples[offset + 2] = 0.40f;
                samples[offset + 3] = 0.50f; // LFE, intentionally ignored.
                samples[offset + 4] = 0.10f;
                samples[offset + 5] = 0.20f;
            }

            WriteWaveExtensible(sourcePath, samples, sampleRate: 1000, channelCount: 6, channelMask: 0x60Fu);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.Equal(2, result.ChannelCount);
            Assert.Equal(2, output.ChannelCount);
            Assert.InRange(output.SampleAt(frame: 100, channel: 0), 0.34f, 0.37f);
            Assert.InRange(output.SampleAt(frame: 100, channel: 1), 0.41f, 0.44f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_downmixes_unmasked_six_channel_source_lane_as_safe_five_one_layout()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source-5-1.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            var samples = new float[1000 * 6];
            for (int frame = 0; frame < 1000; frame++)
            {
                int offset = frame * 6;
                samples[offset + 3] = 0.30f; // LFE in common 5.1 layout; should not enter stereo downmix.
                samples[offset + 5] = 0.20f; // right surround in common 5.1 layout
            }

            WriteWave(sourcePath, samples, sampleRate: 1000, channelCount: 6);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.Equal(2, result.ChannelCount);
            Assert.Equal(2, output.ChannelCount);
            Assert.InRange(output.SampleAt(frame: 100, channel: 0), -0.01f, 0.01f);
            Assert.InRange(output.SampleAt(frame: 100, channel: 1), 0.13f, 0.15f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_downmixes_six_one_source_lane_with_back_center_and_side_channels_to_stereo_output()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source-6-1.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            var samples = new float[1000 * 7];
            for (int frame = 0; frame < 1000; frame++)
            {
                int offset = frame * 7;
                samples[offset + 4] = 0.20f; // back center
                samples[offset + 6] = 0.30f; // right surround
            }

            WriteWave(sourcePath, samples, sampleRate: 1000, channelCount: 7);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            Pcm16Samples output = ReadPcm16Samples(outputPath);
            Assert.Equal(2, result.ChannelCount);
            Assert.Equal(2, output.ChannelCount);
            Assert.InRange(output.SampleAt(frame: 100, channel: 0), 0.13f, 0.15f);
            Assert.InRange(output.SampleAt(frame: 100, channel: 1), 0.34f, 0.36f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_caps_range_to_source_audio_duration_before_allocating_output()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 1.0d, amplitude: 0.2f);
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: -12d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0.25d, EndSeconds: 20d, outputPath),
                TestContext.Current.CancellationToken);

            float[] outputSamples = ReadMonoSamples(outputPath, out int sampleRate);
            Assert.Equal(1000, sampleRate);
            Assert.Equal(750, outputSamples.Length);
            Assert.Equal(0.75d, result.DurationSeconds, precision: 3);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_keeps_missing_take_gap_silent_in_dub_lane()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, amplitude: 0.2f);
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: -12d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.0d, TakeId: null, ArtifactId: null, TakeRelativePath: null, TakeDurationSeconds: 0.5d, IsSilentGap: true, WarningMessage: "Missing take.")
                ],
                DuckingRegions: [],
                Warnings:
                [
                    new MixPlanWarning(0, Guid.NewGuid(), "Missing take.")
                ]);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            float[] outputSamples = ReadMonoSamples(outputPath, out _);
            Assert.All(outputSamples, sample => Assert.InRange(sample, 0.19f, 0.21f));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_fails_when_planned_audible_take_file_is_missing()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string missingTakePath = Path.Join(tempRoot, "missing-take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, amplitude: 0.2f);
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            store.SeedPath("artifacts/tts/missing-take.wav", missingTakePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: -12d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(
                        0,
                        Guid.NewGuid(),
                        0.5d,
                        1.0d,
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        "artifacts/tts/missing-take.wav",
                        0.5d,
                        IsSilentGap: false,
                        WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: []);

            FileNotFoundException exception = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                new PreviewRangeRenderer(store).RenderAsync(
                    new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                    TestContext.Current.CancellationToken));

            Assert.Contains("segment 0", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_reports_specific_parameter_for_invalid_range()
    {
        var renderer = new PreviewRangeRenderer(new FakeArtifactStore());
        var plan = new MixPlan(
            Guid.NewGuid(),
            MediaAssetId: null,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            SourceGainDb: 0d,
            DubbedSpeechGainDb: 0d,
            DuckingGainDb: -12d,
            DuckingLeadSeconds: 0.05d,
            DuckingTailSeconds: 0.18d,
            DateTimeOffset.UtcNow,
            SpeechClips: [],
            DuckingRegions: [],
            Warnings: []);

        ArgumentOutOfRangeException exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            renderer.RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 1d, EndSeconds: 1d, "preview.wav"),
                TestContext.Current.CancellationToken));

        Assert.Equal("EndSeconds", exception.ParamName);
    }

    [Fact]
    public async Task RenderAsync_applies_room_tone_reverb_when_preroll_is_available()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 4.0d, amplitude: 0.3f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.5f);

            Guid projectId = Guid.NewGuid();
            Guid segmentId = Guid.NewGuid();
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                projectId,
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, segmentId, 1.5d, 2.0d, Guid.NewGuid(), Guid.NewGuid(),
                        "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 3d, outputPath),
                TestContext.Current.CancellationToken);

            float[] outputSamples = ReadMonoSamples(outputPath, out int sampleRate);
            Assert.Equal(1000, sampleRate);
            int sampleAtClip = (int)Math.Round(1.7d * sampleRate);
            Assert.NotEqual(0f, outputSamples[sampleAtClip]);
            Assert.Equal(3.0d, result.DurationSeconds, precision: 2);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_writes_hot_five_one_pcm16_at_unit_peak_via_per_track_normalization()
    {
        // 5.1 fixture sums above |1| in downmix without WavePcm16's per-track normalization the
        // output would hard-clip via Math.Clamp and lose dynamic information. Verify L frame peak
        // lands at short.MaxValue while R frame peak is held well below it: proves both that the
        // peak is preserved undistorted and that effective gain was below 1 for non-max frames.
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "hot-5-1.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            const int sampleRate = 1000;
            const int frameCount = 1000;
            const int frameChannels = 6;

            // Unmasked positional 5.1 layout. Alternating per-frame polarities so both positive
            // and negative PCM short ceilings are exercised. Without sign flipping, all output
            // samples would be positive and minLeft/minRight would remain at zero, making any
            // symmetric asserts false-pass traps.
            var samples = new float[frameCount * frameChannels];
            for (int frame = 0; frame < frameCount; frame++)
            {
                float sign = (frame % 2 == 0) ? 1f : -1f;
                int offset = frame * frameChannels;
                samples[offset + 0] = 1.00f * sign; // FL
                samples[offset + 1] = 0.00f;        // FR (zero contributes nothing)
                samples[offset + 2] = 1.00f * sign; // FC -> L+R via DownmixCenterGain (0.707)
                samples[offset + 3] = 1.00f * sign; // LFE -> dropped by downmix
                samples[offset + 4] = 0.50f * sign; // surround L via DownmixSurroundGain (0.707)
                samples[offset + 5] = 1.00f * sign; // surround R via DownmixSurroundGain (0.707)
            }

            WriteWave(sourcePath, samples, sampleRate, channelCount: frameChannels);

            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips: [],
                DuckingRegions: [],
                Warnings: []);

            PreviewRangeRenderResult result = await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);
            Assert.Equal(2, result.ChannelCount);

            // Read back via WavePcm16.ReadAllSamplesAsync so the test stays correct if the writer
            // ever switches to WAVE_FORMAT_EXTENSIBLE headers (header size / data offset differ).
            // The reader normalizes PCM16 shorts by /32768, so peak floats approach 1.0 from below
            // rather than arriving at exactly 1.0; assertion tolerances accommodate that path.
            WavePcm16Samples outputSamples = await WavePcm16.ReadAllSamplesAsync(outputPath, TestContext.Current.CancellationToken);
            Assert.Equal(2, outputSamples.ChannelCount);
            Assert.Equal(1000, outputSamples.SampleRate);

            float peakLeft = 0f;
            float peakRight = 0f;
            float minLeft = 0f;
            float minRight = 0f;
            for (int frame = 0; frame < outputSamples.FrameCount; frame++)
            {
                int frameOffset = frame * outputSamples.ChannelCount;
                float left = outputSamples.Samples[frameOffset];
                float right = outputSamples.Samples[frameOffset + 1];
                if (left > peakLeft) peakLeft = left;
                if (left < minLeft) minLeft = left;
                if (right > peakRight) peakRight = right;
                if (right < minRight) minRight = right;
            }

            // L frame peak preserves full unit amplitude after normalization. The reader divides
            // by 32768 (not 32767), so 32767 PCM short reads back as 0.99997. Tolerance covers the
            // 1 ulp float reciprocal drift. Without per-track scaling, this frame would still hit
            // 1.0 (Math.Clamp) but with hard-clip distortion baked in; the symmetric negative peak
            // proves the per-track scaler preserves sign rather than just abs-truncating.
            Assert.InRange(peakLeft, 0.9999f, 1.0001f);
            Assert.InRange(minLeft, -1.0001f, -0.9999f);

            // R frame peak: post-scale = 1.4142 / 2.0607 ~= 0.6864. Without per-track normalization
            // this channel would clamp to ±1.0; the 0.685..0.690 assertion band detects the fix.
            // Tolerance spans the 1 ulp quantize of PCM16 short /32768 readback path.
            Assert.InRange(peakRight, 0.685f, 0.690f);
            Assert.InRange(minRight, -0.690f, -0.685f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RenderAsync_falls_back_to_dry_take_when_no_preroll_is_available()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string sourcePath = Path.Join(tempRoot, "source.wav");
            string takePath = Path.Join(tempRoot, "take.wav");
            string outputPath = Path.Join(tempRoot, "preview.wav");
            WriteConstantWave(sourcePath, sampleRate: 1000, durationSeconds: 2.0d, amplitude: 0.1f);
            WriteConstantWave(takePath, sampleRate: 1000, durationSeconds: 0.5d, amplitude: 0.4f);

            Guid segmentId = Guid.NewGuid();
            var store = new FakeArtifactStore();
            store.SeedPath(ProjectArtifactPaths.NormalizedAudioRelativePath, sourcePath);
            store.SeedPath("artifacts/tts/take.wav", takePath);
            var plan = new MixPlan(
                Guid.NewGuid(),
                MediaAssetId: null,
                ArtifactKind.NormalizedAudio,
                ProjectArtifactPaths.NormalizedAudioRelativePath,
                SourceGainDb: 0d,
                DubbedSpeechGainDb: 0d,
                DuckingGainDb: 0d,
                DuckingLeadSeconds: 0.05d,
                DuckingTailSeconds: 0.18d,
                DateTimeOffset.UtcNow,
                SpeechClips:
                [
                    new MixSpeechClip(0, segmentId, 0d, 0.5d, Guid.NewGuid(), Guid.NewGuid(),
                        "artifacts/tts/take.wav", 0.5d, IsSilentGap: false, WarningMessage: null)
                ],
                DuckingRegions: [],
                Warnings: []);

            await new PreviewRangeRenderer(store).RenderAsync(
                new PreviewRangeRenderRequest(plan, StartSeconds: 0d, EndSeconds: 1d, outputPath),
                TestContext.Current.CancellationToken);

            float[] outputSamples = ReadMonoSamples(outputPath, out int sampleRate);
            Assert.Equal(1000, sampleRate);
            int sampleInsideTake = (int)Math.Round(0.25d * sampleRate);
            Assert.InRange(outputSamples[sampleInsideTake], 0.45f, 0.55f);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private static MixPlan CreateSingleClipPanRestorePlan(
        string sourceAudioRelativePath,
        string takeRelativePath)
    {
        return new MixPlan(
            Guid.NewGuid(),
            MediaAssetId: null,
            ArtifactKind.Ambiance,
            sourceAudioRelativePath,
            SourceGainDb: 0d,
            DubbedSpeechGainDb: 0d,
            DuckingGainDb: 0d,
            DuckingLeadSeconds: 0.05d,
            DuckingTailSeconds: 0.18d,
            DateTimeOffset.UtcNow,
            SpeechClips:
            [
                new MixSpeechClip(0, Guid.NewGuid(), 0.5d, 1.0d, Guid.NewGuid(), Guid.NewGuid(), takeRelativePath, 0.5d, IsSilentGap: false, WarningMessage: null)
            ],
            DuckingRegions: [],
            Warnings: [],
            OriginalMixAudioRelativePath: ProjectArtifactPaths.NormalizedAudioRelativePath,
            OutputChannelCount: 2,
            RestoreOriginalPan: true);
    }

    private static void WriteConstantWave(string path, int sampleRate, double durationSeconds, float amplitude)
    {
        int sampleCount = (int)Math.Round(sampleRate * durationSeconds);
        var samples = Enumerable.Repeat(amplitude, sampleCount).ToArray();
        WriteMonoWave(path, samples, sampleRate);
    }

    private static void WriteStereoConstantWave(
        string path,
        int sampleRate,
        double durationSeconds,
        float leftAmplitude,
        float rightAmplitude)
    {
        int frameCount = (int)Math.Round(sampleRate * durationSeconds);
        var samples = new float[frameCount * 2];
        for (int frame = 0; frame < frameCount; frame++)
        {
            samples[(frame * 2) + 0] = leftAmplitude;
            samples[(frame * 2) + 1] = rightAmplitude;
        }

        WriteWave(path, samples, sampleRate, channelCount: 2);
    }

    private static void WriteMonoWave(string path, IReadOnlyList<float> samples, int sampleRate)
    {
        WriteWave(path, samples, sampleRate, channelCount: 1);
    }

    private static void WriteWave(string path, IReadOnlyList<float> samples, int sampleRate, int channelCount)
    {
        int dataLength = samples.Count * sizeof(short);
        var header = new byte[44];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), (short)channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * channelCount * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), (short)(channelCount * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);

        var data = new byte[dataLength];
        for (int i = 0; i < samples.Count; i++)
        {
            int pcm = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32768f);
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(i * sizeof(short), sizeof(short)),
                (short)Math.Clamp(pcm, short.MinValue, short.MaxValue));
        }

        File.WriteAllBytes(path, header.Concat(data).ToArray());
    }

    private static void WriteWaveExtensible(
        string path,
        IReadOnlyList<float> samples,
        int sampleRate,
        int channelCount,
        uint channelMask)
    {
        int dataLength = samples.Count * sizeof(short);
        var header = new byte[68];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 60 + dataLength);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 40);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), 0xFFFE);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), (short)channelCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * channelCount * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), (short)(channelCount * sizeof(short)));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(36, 2), 22);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(38, 2), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40, 4), channelMask);
        byte[] pcmSubFormatGuid =
        [
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00,
            0x10, 0x00,
            0x80, 0x00,
            0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
        ];
        pcmSubFormatGuid.CopyTo(header.AsSpan(44));
        Encoding.ASCII.GetBytes("data").CopyTo(header, 60);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(64, 4), dataLength);

        var data = new byte[dataLength];
        for (int i = 0; i < samples.Count; i++)
        {
            int pcm = (int)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32768f);
            BinaryPrimitives.WriteInt16LittleEndian(
                data.AsSpan(i * sizeof(short), sizeof(short)),
                (short)Math.Clamp(pcm, short.MinValue, short.MaxValue));
        }

        File.WriteAllBytes(path, header.Concat(data).ToArray());
    }

    private sealed record Pcm16Samples(int SampleRate, int ChannelCount, float[] Samples)
    {
        public float SampleAt(int frame, int channel) => Samples[(frame * ChannelCount) + channel];
    }

    private static Pcm16Samples ReadPcm16Samples(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int channelCount = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(22, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4));
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
        var samples = new float[dataLength / sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (i * sizeof(short)), sizeof(short))) / 32768f;
        }

        return new Pcm16Samples(sampleRate, channelCount, samples);
    }

    private static float[] ReadMonoSamples(string path, out int sampleRate)
    {
        byte[] bytes = File.ReadAllBytes(path);
        sampleRate = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(24, 4));
        int dataLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(40, 4));
        var samples = new float[dataLength / sizeof(short)];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(44 + (i * sizeof(short)), sizeof(short))) / 32768f;
        }

        return samples;
    }
}
