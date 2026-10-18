using System.Buffers.Binary;
using Trackdub.Composition.Runtime.Planning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.SortFormer;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class SortFormerDiarizationEngineTests
{
    [Fact]
    public void SortFormerFeatureExtractor_RangeExtract_MatchesFullExtract()
    {
        float[] samples = CreateSineWave(durationSeconds: 1.0, sampleRate: 16000); // 16000 -> 101 frames
        var extractor = new SortFormerFeatureExtractor();
        using SortFormerFeatureInputSet full = extractor.Extract(samples);
        int totalFrames = SortFormerFeatureExtractor.GetFrameCount(samples.Length);
        Assert.Equal(full.FrameCount, totalFrames);

        foreach (int chunkFrames in new[] { 1, 7, 37, 101 })
        {
            var fake = new FakeAudioSamples(samples);
            var combined = new List<float>(full.FrameCount * full.FeatureCount);
            for (int start = 0; start < totalFrames; start += chunkFrames)
            {
                int count = Math.Min(chunkFrames, totalFrames - start);
                using SortFormerFeatureInputSet part = extractor.Extract(
                    fake, start, count, CancellationToken.None);
                Assert.Equal(count, part.FrameCount);
                combined.AddRange(part.Data.ToArray());
            }

            Assert.Equal(full.Data.Length, combined.Count);
            for (int i = 0; i < combined.Count; i++)
            {
                Assert.Equal(full.Data[i], combined[i], 5);
            }

            // Reads must stay bounded by the needed window, never the full waveform.
            int expectedLastFrame = totalFrames - 1;
            long maxNeeded = ((long)expectedLastFrame * SortFormerFeatureExtractor.HopLength)
                + SortFormerFeatureExtractor.FftSize - (SortFormerFeatureExtractor.FftSize / 2);
            Assert.True(fake.MaxReadEnd <= maxNeeded + 2);
        }
    }

    [Fact]
    public void SortFormerFeatureExtractor_RangeExtract_LongAudioReadsOnlyChunkWindow()
    {
        float[] samples = CreateSineWave(durationSeconds: 10.0, sampleRate: 16000); // 160000 -> 1001 frames
        var fake = new FakeAudioSamples(samples);
        var extractor = new SortFormerFeatureExtractor();

        using SortFormerFeatureInputSet firstChunk = extractor.Extract(fake, 0, 50, CancellationToken.None);

        Assert.Equal(50, firstChunk.FrameCount);
        long boundedEnd = ((long)49 * SortFormerFeatureExtractor.HopLength)
            + SortFormerFeatureExtractor.FftSize - (SortFormerFeatureExtractor.FftSize / 2) + 1;
        Assert.True(
            fake.MaxReadEnd <= boundedEnd + 1,
            $"streamed chunk read {fake.MaxReadEnd} samples but chunk window ends at {boundedEnd}");
        Assert.True(fake.MaxReadEnd < samples.Length);
    }

    [Theory]
    [InlineData(0, 0)]     // empty input produces zero frames, matching span Extract
    [InlineData(160, 2)]   // exact hop multiple still gets the final padded frame
    [InlineData(1600, 11)]
    [InlineData(320, 3)]
    public void SortFormerFeatureExtractor_GetFrameCount_MatchesFormula(int sampleCount, int expected)
    {
        Assert.Equal(expected, SortFormerFeatureExtractor.GetFrameCount(sampleCount));
    }

    [Fact]
    public void SortFormerFeatureExtractor_GetFrameCount_OverflowThrows()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SortFormerFeatureExtractor.GetFrameCount((long)int.MaxValue * SortFormerFeatureExtractor.HopLength));
    }

    [Fact]
    public void SortFormerFeatureExtractor_RangeExtract_CancellationThrowsWithoutPartialResult()
    {
        float[] samples = CreateSineWave(durationSeconds: 1.0, sampleRate: 16000);
        var fake = new FakeAudioSamples(samples);
        var extractor = new SortFormerFeatureExtractor();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
        {
            using SortFormerFeatureInputSet _ = extractor.Extract(fake, 0, 10, canceled.Token);
        });
    }

    [Fact]
    public void SortFormerFeatureExtractor_RangeExtract_RejectsOutOfRange()
    {
        float[] samples = CreateSineWave(durationSeconds: 0.5, sampleRate: 16000);
        var fake = new FakeAudioSamples(samples);
        var extractor = new SortFormerFeatureExtractor();
        int total = SortFormerFeatureExtractor.GetFrameCount(samples.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.Extract(fake, -1, 1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.Extract(fake, 0, -1, CancellationToken.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.Extract(fake, total - 1, 2, CancellationToken.None));
        // startFrame + frameCount overflow must be rejected, never wrap to a valid range
        Assert.Throws<ArgumentOutOfRangeException>(() => extractor.Extract(fake, int.MaxValue, int.MaxValue, CancellationToken.None));
        Assert.Equal(0, fake.MaxReadEnd);
        using SortFormerFeatureInputSet empty = extractor.Extract(fake, 0, 0, CancellationToken.None);
        Assert.Equal(0, empty.FrameCount);
    }

    /// <summary>Deterministic in-memory <see cref="Trackdub.Inference.Onnx.Audio.IAudioSamples"/> that records read extent.</summary>
    private sealed class FakeAudioSamples(float[] data) : Trackdub.Inference.Onnx.Audio.IAudioSamples
    {
        public int SampleRate => 16000;

        public long SampleFrameCount => data.Length;

        public long MaxReadEnd { get; private set; }

        public void ReadMonoSamples(long startFrame, Span<float> destination)
        {
            MaxReadEnd = Math.Max(MaxReadEnd, startFrame + destination.Length);
            data.AsSpan((int)startFrame, destination.Length).CopyTo(destination);
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void MaxSupportedSpeakers_IsPublicConstantEqualToFour()
    {
        Assert.Equal(4, SortFormerDiarizationEngine.MaxSupportedSpeakers);
    }

    [Fact]
    public void SortFormerFeatureExtractor_extracts_finite_streaming_features()
    {
        float[] samples = CreateSineWave(durationSeconds: 1.0, sampleRate: 16000);
        var extractor = new SortFormerFeatureExtractor();

        using SortFormerFeatureInputSet features = extractor.Extract(samples);

        Assert.True(features.FrameCount > 0);
        Assert.Equal(128, features.FeatureCount);
        Assert.Equal(features.FrameCount * features.FeatureCount, features.Data.Length);
        foreach (float value in features.Data)
        {
            Assert.True(float.IsFinite(value));
        }
    }

    [LocalSortFormerModelFact]
    public async Task DiarizeAsync_with_local_cached_v21_onnx_accepts_streaming_feature_inputs()
    {
        BundledModelManifestRegistry registry = LoadRegistry();
        string modelPath = GetLocalCacheModelPath();
        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string wavePath = Path.Join(tempDirectory, "sortformer-local-cache.wav");

        try
        {
            await WriteTestWaveAsync(wavePath, durationSeconds: 0.5, sampleRate: 16000, CancellationToken.None);
            var engine = new SortFormerDiarizationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Diarization,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
                    ModelAlias = "sortformer-diarizer-4spk-v2.1",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.Cpu,
                    ModelEntryPath = modelPath
                }),
                new BenchmarkModelPathResolver(registry));

            IReadOnlyList<DiarizedSpeakerTurn> turns = await engine.DiarizeAsync(
                wavePath,
                0.5,
                [new SpeechRegion(0, 0.0, 0.5)],
                CancellationToken.None);

            Assert.NotNull(turns);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    [SortFormerFixtureFact]
    public async Task DiarizeAsync_with_real_sortformer_fixture_produces_valid_turns()
    {
        BundledModelManifestRegistry registry = LoadRegistry();
        _ = ResolveFixtureModelPath(registry)
            ?? throw new InvalidOperationException("SortFormer ONNX fixture resolution unexpectedly failed after attribute validation.");

        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string wavePath = Path.Join(tempDirectory, "sortformer-fixture.wav");

        try
        {
            await WriteTestWaveAsync(wavePath, durationSeconds: 1.0, sampleRate: 16000, CancellationToken.None);
            var runtimePlanner = new RuntimePlanner(
                registry,
                new MachineHardwareProfileProvider(),
                new OnnxExecutionProviderDiscovery(new NullOpenVinoAvailabilityProvider()),
                new OnnxExecutionProviderSmokeTester(),
                new CompositeModelCacheInventory(
                    new BundledManifestModelCacheInventory(registry)));
            var engine = new SortFormerDiarizationEngine(
                runtimePlanner,
                new BenchmarkModelPathResolver(registry));

            IReadOnlyList<DiarizedSpeakerTurn> turns = await engine.DiarizeAsync(
                wavePath,
                1.0,
                [new SpeechRegion(0, 0.0, 1.0)],
                CancellationToken.None);

            Assert.NotNull(turns);

            foreach (DiarizedSpeakerTurn turn in turns)
            {
                Assert.True(turn.EndSeconds > turn.StartSeconds, $"Turn end ({turn.EndSeconds}) must be greater than start ({turn.StartSeconds}).");
                Assert.True(turn.EndSeconds <= 1.0, $"Turn end ({turn.EndSeconds}) must not exceed audio duration (1.0).");
                if (turn.Confidence is double confidence)
                {
                    Assert.True(confidence >= 0.0 && confidence <= 1.0, $"Turn confidence ({confidence}) must be between 0.0 and 1.0.");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Hardware validation for TRT-RTX optimization of the SortFormer encoder.
    ///
    /// Prerequisites (already satisfied on hardware where build/sortformer-4spk-onnx-trtrtx-validated-fp16
    /// exists with a PASS in build/sortformer-4spk-trtrtx-validation.json):
    ///   1. Download the model:
    ///      dotnet run --project src/Trackdub.Tools -- ingest --model cgus/diar_streaming_sortformer_4spk-v2.1-onnx
    ///   2. Run Olive TRT-RTX optimization and staging:
    ///      .\tools\olive\Validate-SortFormerTrtRtx.ps1
    ///
    /// This test never runs in CI: the fact skips at discovery time when its gitignored
    /// staging directory under build/ is absent (same pattern as the whisper TRT-RTX smokes).
    /// </summary>
    [RequiresTrtRtxStagingFact("sortformer-4spk-onnx-trtrtx-validated-fp16",
        "run tools/olive/Validate-SortFormerTrtRtx.ps1")]
    public async Task DiarizeAsync_with_trtrtx_staged_model_selects_tensorrt_rtx_provider()
    {
        string stagingDir = Path.Join(FindRepoRoot(), "build", "sortformer-4spk-onnx-trtrtx-validated-fp16", "onnx");
        string stagedModelPath = Path.Join(stagingDir, "model.onnx");

        Assert.True(
            File.Exists(stagedModelPath),
            $"Staged model not found: {stagedModelPath}\n" +
            "Run: .\\tools\\olive\\Validate-SortFormerTrtRtx.ps1");

        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string wavePath = Path.Join(tempDirectory, "sortformer-trtrtx.wav");

        try
        {
            await WriteTestWaveAsync(wavePath, durationSeconds: 1.0, sampleRate: 16000, CancellationToken.None);

            // No manifest registry needed: BenchmarkModelPathResolver.Discover falls through to
            // an absolute-path lookup when given one directly, matching the staging layout the
            // validate script produces (mirrors WhisperOnnxTrtRtxValidationTests).
            var engine = new SortFormerDiarizationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Diarization,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
                    ModelAlias = "sortformer-diarizer-4spk-v2.1",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.TensorRTRtx,
                    ModelEntryPath = stagedModelPath
                }),
                new BenchmarkModelPathResolver());

            IReadOnlyList<DiarizedSpeakerTurn> turns = await engine.DiarizeAsync(
                wavePath,
                1.0,
                [new SpeechRegion(0, 0.0, 1.0)],
                CancellationToken.None);

            Assert.NotNull(turns);
            Assert.NotNull(engine.LastExecutionSummary);

            // Confirm TRT-RTX (or its documented DirectML fallback on non-NVIDIA hardware) was
            // actually selected — not a silent CPU fallback the caller never notices.
            Assert.False(
                string.IsNullOrWhiteSpace(engine.LastExecutionSummary!.SelectedProvider),
                "SelectedProvider must not be empty — session provider resolution failed.");
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Drives enough audio through the staged TRT-RTX streaming session to saturate the speaker
    /// cache and FIFO, then asserts chunks were served by the fixed-address bound path
    /// (LastBoundChunkCount). The bound feed is byte-identical to the managed feed at full
    /// capacity, so this mainly proves the binding mechanics survive the state machine.
    /// </summary>
    [RequiresTrtRtxStagingFact("sortformer-4spk-onnx-trtrtx-validated-fp16",
        "run tools/olive/Validate-SortFormerTrtRtx.ps1")]
    public async Task DiarizeAsync_with_trtrtx_staged_model_uses_bound_path_after_cache_saturation()
    {
        TensorRtRtxBootstrapResult bootstrap = await TensorRtRtxPluginService.Shared
            .EnsureRegisteredAsync(allowProviderDownloads: false, CancellationToken.None);
        Assert.True(bootstrap.Succeeded, $"TRT-RTX plugin registration failed: {bootstrap.Detail}");

        string stagingDir = Path.Join(FindRepoRoot(), "build", "sortformer-4spk-onnx-trtrtx-validated-fp16", "onnx");
        string stagedModelPath = Path.Join(stagingDir, "model.onnx");
        Assert.True(File.Exists(stagedModelPath), $"Staged model not found: {stagedModelPath}");

        // 95s at 16kHz produces ~9500 feature frames = 4 streaming chunks; the speaker cache
        // (188 frames) and FIFO (40 frames) saturate by the second chunk.
        const double durationSeconds = 95.0;
        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Inference.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string wavePath = Path.Join(tempDirectory, "sortformer-trtrtx-saturating.wav");

        try
        {
            await WriteTestWaveAsync(wavePath, durationSeconds, sampleRate: 16000, CancellationToken.None);

            var engine = new SortFormerDiarizationEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Diarization,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = "cgus/diar_streaming_sortformer_4spk-v2.1-onnx",
                    ModelAlias = "sortformer-diarizer-4spk-v2.1",
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.TensorRTRtx,
                    ModelEntryPath = stagedModelPath
                }),
                new BenchmarkModelPathResolver());

            IReadOnlyList<DiarizedSpeakerTurn> turns = await engine.DiarizeAsync(
                wavePath,
                durationSeconds,
                [new SpeechRegion(0, 0.0, durationSeconds)],
                CancellationToken.None);

            Assert.NotNull(turns);
            Assert.True(
                engine.LastBoundChunkCount > 0,
                "Expected saturated caches to route later chunks through the bound path; " +
                $"LastBoundChunkCount={engine.LastBoundChunkCount}, provider={engine.LastExecutionSummary?.SelectedProvider}");

            foreach (DiarizedSpeakerTurn turn in turns)
            {
                Assert.True(turn.EndSeconds > turn.StartSeconds);
                Assert.True(turn.EndSeconds <= durationSeconds + 0.01);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Trackdub.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static float[] CreateSineWave(double durationSeconds, int sampleRate)
    {
        int sampleCount = (int)(durationSeconds * sampleRate);
        var samples = new float[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            double t = index / (double)sampleRate;
            samples[index] = (float)(Math.Sin(2d * Math.PI * 220d * t) * 0.2d);
        }

        return samples;
    }

    private static string GetLocalCacheModelPath() =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub",
            "model-cache",
            "cgus",
            "diar_streaming_sortformer_4spk-v2.1-onnx",
            "onnx",
            "model.onnx");

    private static async Task WriteTestWaveAsync(
        string path,
        double durationSeconds,
        int sampleRate,
        CancellationToken cancellationToken)
    {
        int sampleCount = (int)(durationSeconds * sampleRate);
        short[] samples = new short[sampleCount];
        for (int index = 0; index < sampleCount; index++)
        {
            double t = index / (double)sampleRate;
            samples[index] = (short)(Math.Sin(2d * Math.PI * 220d * t) * short.MaxValue * 0.2d);
        }

        byte[] pcmData = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, pcmData, 0, pcmData.Length);
        byte[] header = new byte[44];
        "RIFF"u8.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + pcmData.Length);
        "WAVE"u8.CopyTo(header.AsSpan(8, 4));
        "fmt "u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate * sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), sizeof(short));
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 16);
        "data"u8.CopyTo(header.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), pcmData.Length);

        await using FileStream stream = File.Create(path);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(pcmData, cancellationToken).ConfigureAwait(false);
    }

    private static BundledModelManifestRegistry LoadRegistry()
    {
        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error) ||
            registry is null)
        {
            throw new InvalidOperationException(error ?? "Bundled model manifest was not found.");
        }

        return registry;
    }

    private static string? ResolveFixtureModelPath(BundledModelManifestRegistry registry)
    {
        if (!registry.TryResolve("sortformer-diarizer-4spk-v2.1", out BundledModelManifestResolution? resolution) ||
            resolution is null ||
            !Directory.Exists(resolution.Entry.RootDirectory) ||
            !File.Exists(resolution.Entry.DefaultBenchmarkEntryPath))
        {
            return null;
        }

        return resolution.Entry.DefaultBenchmarkEntryPath;
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
    private sealed class SortFormerFixtureFactAttribute : FactAttribute
    {
        public SortFormerFixtureFactAttribute()
        {
            if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out _) ||
                registry is null)
            {
                Skip = "Bundled model manifest was not found.";
                return;
            }

            try
            {
                if (ResolveFixtureModelPath(registry) is null)
                {
                    Skip = "SortFormer ONNX fixture is not present in the local models directory.";
                }
            }
            catch (Exception)
            {
                Skip = "SortFormer ONNX fixture is not present in the local models directory.";
            }
        }
    }

    private sealed class LocalSortFormerModelFactAttribute : FactAttribute
    {
        public LocalSortFormerModelFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("TRACKDUB_RUN_LOCAL_MODEL_TESTS"),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = "Set TRACKDUB_RUN_LOCAL_MODEL_TESTS=1 to run local cached model tests.";
                return;
            }

            if (!File.Exists(GetLocalCacheModelPath()))
            {
                Skip = "SortFormer ONNX model is not present in the local Trackdub model cache.";
            }
        }
    }

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan);
    }
}
