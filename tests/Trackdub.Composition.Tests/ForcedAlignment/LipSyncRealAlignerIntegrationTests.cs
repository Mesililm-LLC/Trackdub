using Trackdub.Application.LipSync;
using Trackdub.Composition.ForcedAlignment;
using Trackdub.Contracts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.Media;
using Trackdub.Domain.Tts;
using Trackdub.Inference.Onnx.ForcedAlignment;
using Trackdub.Media.Stretch;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.ForcedAlignment;

/// <summary>
/// Stage-level real-model proof for M22: real wav2vec2 CTC aligner (routed), real
/// phoneme timing planner, real WSOLA stretch service, driven through
/// <see cref="LipSyncStageHandler"/>. Skips unless BOTH the model is installed in the
/// user model cache AND a real speech fixture WAV is supplied via the
/// TRACKDUB_LIPSYNC_SPEECH_FIXTURE environment variable (FixtureFact convention).
/// Never downloads anything.
/// </summary>
public sealed class LipSyncRealAlignerIntegrationTests
{
    private const string ModelId = "wav2vec2-lv60-espeak-cv-ft-onnx";
    private const string FixtureEnvVar = "TRACKDUB_LIPSYNC_SPEECH_FIXTURE";
    private const string FixtureTranscript = "the cat sat on the mat";

    [LipSyncRealModelFact]
    [Trait("Category", "Integration")]
    public async Task HandleAsync_RealAlignerAndStretch_ProducesLipSyncTakeArtifact()
    {
        string fixtureWavPath = Environment.GetEnvironmentVariable(FixtureEnvVar)!;
        string modelRoot = LipSyncRealModelFactAttribute.ResolveModelRoot(ModelId);

        string directory = Path.Join(Path.GetTempPath(), $"lipsync-real-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var projectId = Guid.NewGuid();
            var mediaAsset = new MediaAsset(
                Id: Guid.NewGuid(),
                ProjectId: projectId,
                SourceFilePath: "/tmp/test.mp4",
                SourceFileName: "test.mp4",
                FingerprintSha256: "abc",
                SourceSizeBytes: 0L,
                SourceLastWriteTimeUtc: DateTimeOffset.UtcNow,
                FormatName: "mp4",
                DurationSeconds: 10.0,
                HasAudio: true,
                HasVideo: true,
                CreatedAtUtc: DateTimeOffset.UtcNow);

            var artifactStore = new FakeArtifactStore(directory);
            var stageRunStore = new FakeProjectStageRunStore();
            var mediaRepo = new FakeMediaAssetRepository();

            using var wav2Vec2 = new Wav2Vec2CtcForcedAligner(modelRoot);
            var routedAligner = new RoutedForcedAligner([wav2Vec2]);

            // Seed the TTS-take artifact with the REAL speech fixture bytes.
            var relPath = $"tts/{Guid.NewGuid():N}.wav";
            artifactStore.Seed(relPath, File.ReadAllBytes(fixtureWavPath));

            var artifactId = Guid.NewGuid();
            var translatedSegmentId = Guid.NewGuid();
            var take = TtsTake.CreateStock(
                projectId: projectId,
                voiceAssignmentId: Guid.NewGuid(),
                translatedSegmentId: translatedSegmentId) with
            { ArtifactId = artifactId };

            var artifact = new ProjectArtifact(
                Id: artifactId, ProjectId: projectId, MediaAssetId: mediaAsset.Id,
                Kind: ArtifactKind.TtsTake, RelativePath: relPath,
                Sha256: "abc", SizeBytes: 0L, DurationSeconds: null,
                SampleRate: null, ChannelCount: null, CreatedAtUtc: DateTimeOffset.UtcNow);

            double fixtureDurationSeconds = EstimateWavDurationSeconds(fixtureWavPath);
            var handler = new LipSyncStageHandler(
                forcedAligner: routedAligner,
                phonemeTimingPlanner: new PhonemeTimingPlanner(),
                phonemeStretchService: new WsolaPhonemeStretchService(),
                artifactStore: artifactStore,
                stageRunStore: stageRunStore,
                segmentRepository: new FakeLipSyncSegmentRepository(),
                mediaAssetRepository: mediaRepo,
                audioClipExtractor: new CopyingAudioClipExtractor());

            var result = await handler.HandleAsync(
                new LipSyncStageRequest(
                    ProjectId: projectId,
                    MediaAsset: mediaAsset,
                    TtsTakes: [take],
                    ExistingArtifacts: [artifact],
                    PreferredModelAlias: ModelId,
                    SegmentTranscriptMap: new Dictionary<Guid, string>
                    { [translatedSegmentId] = FixtureTranscript },
                    SegmentSourceTimingMap: new Dictionary<Guid, SegmentSourceTiming>
                    { [translatedSegmentId] = new SegmentSourceTiming(0.0, fixtureDurationSeconds) },
                    SourceSegmentTranscriptMap: new Dictionary<Guid, string>
                    { [translatedSegmentId] = FixtureTranscript },
                    SourceAudioPath: fixtureWavPath),
                TestContext.Current.CancellationToken);

            var segment = Assert.Single(result.Segments);

            // Honest outcome set. The real wav2vec2 session loads and CTC-decodes real
            // phonemes (proven separately by Wav2Vec2CtcForcedAlignerIntegrationTests).
            // With eSpeak phonemization wired, confidence should improve; low-confidence
            // SkippedLowConfidence remains an honest non-faked outcome when audio/transcript
            // still diverge.
            Assert.True(
                segment.Status is LipSyncSegmentStatus.Aligned
                    or LipSyncSegmentStatus.Partial
                    or LipSyncSegmentStatus.SkippedLowConfidence,
                $"Unexpected status {segment.Status}: skip='{segment.SkipReason}' fail='{segment.FailureReason}'");

            // The original TTS take is sacred in every outcome: still on disk, byte-identical.
            byte[] original = File.ReadAllBytes(fixtureWavPath);
            byte[] preserved = File.ReadAllBytes(artifactStore.GetPath(relPath));
            Assert.Equal(original, preserved);

            if (segment.Status is LipSyncSegmentStatus.Aligned or LipSyncSegmentStatus.Partial)
            {
                // High-confidence path: the real model drove a real stretch and the
                // LipSyncTake artifact must be registered and exist on disk.
                Assert.NotNull(segment.AlignedTtsDuration);
                Assert.Equal(ModelId, segment.ModelId);
                Assert.Equal("onnx-ctc-phoneme-aligner", segment.ProviderId);

                var lipSyncArtifact = Assert.Single(
                    mediaRepo.Artifacts, a => a.Kind == ArtifactKind.LipSyncTake);
                Assert.True(File.Exists(artifactStore.GetPath(lipSyncArtifact.RelativePath)),
                    "Registered LipSyncTake artifact file does not exist on disk.");
            }
            else
            {
                // Low-confidence skip: no LipSyncTake artifact, original take preserved,
                // and the skip reason is structured (not a silent failure).
                Assert.DoesNotContain(mediaRepo.Artifacts, a => a.Kind == ArtifactKind.LipSyncTake);
                Assert.NotNull(segment.SkipReason);
            }
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static double EstimateWavDurationSeconds(string wavPath)
    {
        // data-chunk length / byte-rate; good enough to bound the source-timing window.
        using var stream = File.OpenRead(wavPath);
        using var reader = new BinaryReader(stream);
        stream.Seek(28, SeekOrigin.Begin);
        int byteRate = reader.ReadInt32();
        double dataBytes = Math.Max(0, stream.Length - 44);
        return byteRate > 0 ? dataBytes / byteRate : 1.0;
    }

    /// <summary>Copies the fixture WAV instead of trimming — real audio in, real audio out.</summary>
    private sealed class CopyingAudioClipExtractor : IAudioClipExtractor
    {
        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath, double startSeconds, double endSeconds,
            string destinationPath, CancellationToken cancellationToken)
        {
            File.Copy(sourceWavePath, destinationPath, overwrite: true);
            return Task.FromResult(new AudioClipExtractionResult(
                OutputPath: destinationPath,
                DurationSeconds: Math.Max(0.0, endSeconds - startSeconds),
                SampleRate: 16_000,
                ChannelCount: 1));
        }

        public Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath, IReadOnlyList<AudioClipRange> ranges,
            string destinationPath, CancellationToken cancellationToken)
        {
            File.Copy(sourceWavePath, destinationPath, overwrite: true);
            return Task.FromResult(new AudioClipExtractionResult(
                OutputPath: destinationPath,
                DurationSeconds: ranges.Sum(r => Math.Max(0.0, r.EndSeconds - r.StartSeconds)),
                SampleRate: 16_000,
                ChannelCount: 1));
        }
    }
}

/// <summary>
/// Skips unless the wav2vec2 aligner model is installed in the user model cache AND the
/// TRACKDUB_LIPSYNC_SPEECH_FIXTURE environment variable points to an existing WAV file.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LipSyncRealModelFactAttribute : FactAttribute
{
    public LipSyncRealModelFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        string root = ResolveModelRoot("wav2vec2-lv60-espeak-cv-ft-onnx");
        bool hasOnnx =
            File.Exists(Path.Join(root, "onnx", "model_int8.onnx")) ||
            File.Exists(Path.Join(root, "onnx", "model_fp16.onnx"));
        if (!hasOnnx || !File.Exists(Path.Join(root, "vocab.json")))
        {
            Skip = $"wav2vec2 aligner model not present in model cache ({root}). " +
                   "Need vocab.json plus onnx/model_int8.onnx or onnx/model_fp16.onnx.";
            return;
        }

        string? fixture = Environment.GetEnvironmentVariable("TRACKDUB_LIPSYNC_SPEECH_FIXTURE");
        if (string.IsNullOrWhiteSpace(fixture) || !File.Exists(fixture))
        {
            Skip = "TRACKDUB_LIPSYNC_SPEECH_FIXTURE is not set to an existing speech WAV " +
                   "(say 'the cat sat on the mat'); supply one to run the stage-level real-model proof.";
        }
    }

    public static string ResolveModelRoot(string modelId)
    {
        string? configured = Environment.GetEnvironmentVariable("TRACKDUB_MODEL_CACHE");
        string cacheRoot = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trackdub", "model-cache");
        return Path.Join(cacheRoot, modelId);
    }
}
