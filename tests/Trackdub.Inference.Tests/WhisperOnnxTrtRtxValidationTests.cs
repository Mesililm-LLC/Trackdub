using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Onnx.Whisper;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Skips unless the local TRT-RTX staging directory for the model size exists, so the
/// hardware-validation smokes below only run on a machine that produced it via
/// <c>tools/olive/Validate-WhisperOnnxTrtRtx.ps1</c> and never fail in CI.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresTrtRtxStagingFactAttribute : FactAttribute
{
    public RequiresTrtRtxStagingFactAttribute(string modelSize)
        : this(
            $"whisper-{modelSize}-onnx-trtrtx-validated",
            $"run tools/olive/Validate-WhisperOnnxTrtRtx.ps1 -ModelSize {modelSize}")
    {
    }

    public RequiresTrtRtxStagingFactAttribute(string stagingDirectoryName, string fixHint)
    {
        // TryFindRepoRoot rather than the throwing FindRepoRoot: an exception escaping the
        // constructor during xunit v2 discovery can cost the whole assembly's test run, while
        // a null root here is just another Skip reason.
        string? repoRoot = TestRepoRootResolver.TryFindRepoRoot();
        if (repoRoot is null)
        {
            Skip = "Unable to locate Trackdub.slnx from the test runner base directory; skipping TRT-RTX staging smoke.";
            return;
        }

        // TrimStart keeps rooted segments from silently dropping the repo root
        // (Path.Join argument-drop guard); segments here are literals or enum-derived.
        string stagingDirectory = Path.Join(
            repoRoot,
            NormalizeRelativeSegment("build"),
            NormalizeRelativeSegment(stagingDirectoryName));
        if (!Directory.Exists(stagingDirectory))
        {
            Skip = $"TRT-RTX staging directory not found: {stagingDirectory} ({fixHint}).";
        }
    }

    private static string NormalizeRelativeSegment(string segment) =>
        segment.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

/// <summary>
/// Hardware validation tests for TRT-RTX optimization of whisper-onnx models.
///
/// Prerequisites before running any test here:
///   1. Download the whisper-onnx model:
///      dotnet run --project src/Trackdub.Tools -- ingest --model onnx-community/whisper-{size}
///   2. Run Olive TRT-RTX optimization and staging:
///      .\tools\olive\Validate-WhisperOnnxTrtRtx.ps1 -ModelSize {size}
///   3. Verify build/whisper-{size}-onnx-trtrtx-validated/ was created, then run:
///      dotnet test tests/Trackdub.Inference.Tests --filter "FullyQualifiedName~WhisperOnnxTrtRtx"
///   4. If tests pass, apply manifest+test flip:
///      .\tools\olive\Flip-WhisperOnnxTrtRtx.ps1
///
/// These tests never run in CI: each fact skips at discovery time when its gitignored
/// staging directory under build/ is absent.
/// </summary>
public sealed class WhisperOnnxTrtRtxValidationTests
{
    [RequiresTrtRtxStagingFact("tiny")]
    public async Task WhisperOnnxTrtRtx_TinyModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("tiny", "onnx-community/whisper-tiny");
    }

    [RequiresTrtRtxStagingFact("base")]
    public async Task WhisperOnnxTrtRtx_BaseModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("base", "onnx-community/whisper-base");
    }

    [RequiresTrtRtxStagingFact("small")]
    public async Task WhisperOnnxTrtRtx_SmallModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("small", "onnx-community/whisper-small");
    }

    [RequiresTrtRtxStagingFact("medium")]
    public async Task WhisperOnnxTrtRtx_MediumModel_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("medium", "Xenova/whisper-medium");
    }

    [RequiresTrtRtxStagingFact("large-v3")]
    public async Task WhisperOnnxTrtRtx_LargeV3Model_SessionLoadsAndTranscribesSilence()
    {
        await RunTrtRtxSilenceSmokeAsync("large-v3", "Xenova/whisper-large-v3");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Shared implementation
    // ─────────────────────────────────────────────────────────────────────────

    private static async Task RunTrtRtxSilenceSmokeAsync(string modelSize, string modelId)
    {
        string stagingDir = Path.Join(FindRepoRoot(), "build", $"whisper-{modelSize}-onnx-trtrtx-validated");

        string wavePath = CreateSilenceWaveFile(durationSeconds: 1.0);
        try
        {
            // No manifest registry needed: Discover() falls through to directory scan when given
            // an absolute path, matching the staging layout the validate script produces.
            var resolver = new BenchmarkModelPathResolver();

            var engine = new WhisperOnnxAudioTranscriptionEngine(
                new StubRuntimePlanner(new StageRuntimePlan
                {
                    Stage = RuntimeStage.Asr,
                    Status = StageRuntimePlanStatus.Ready,
                    ModelId = modelId,
                    ModelAlias = stagingDir,
                    Variant = "default",
                    ExecutionProvider = ExecutionProviderKind.TensorRTRtx
                }),
                resolver);

            IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
                wavePath,
                [new SpeechRegion(0, 0.0, 0.8)],
                CancellationToken.None);

            // Silence legitimately decodes to empty text on larger models (CPU does the same);
            // this smoke only proves the TRT-RTX sessions ran end to end.
            Assert.Single(segments);
            Assert.NotNull(engine.LastExecutionSummary);

            // Confirm TRT-RTX (or its DirectML fallback on non-NVIDIA hardware) was selected.
            Assert.False(
                string.IsNullOrWhiteSpace(engine.LastExecutionSummary!.SelectedProvider),
                "SelectedProvider must not be empty — session provider resolution failed.");
        }
        finally
        {
            if (File.Exists(wavePath))
                File.Delete(wavePath);
        }
    }

    /// <summary>
    /// Real-speech parity check for the fixed-address bound path: the same pooled TRT-RTX
    /// sessions transcribe the 6s fixture once through OrtIoBinding (padded decoder inputs) and
    /// once through managed Run. Token-level equality proves the padded logits indexing and
    /// buffer reuse do not perturb decoding.
    /// </summary>
    [RequiresTrtRtxStagingFact("tiny")]
    [Trait("Category", "Integration")]
    public async Task WhisperOnnxTrtRtx_TinyModel_BoundPathMatchesManagedOnRealSpeech()
    {
        await EnsureTrtRtxPluginRegisteredAsync();

        string stagingDir = Path.Join(FindRepoRoot(), "build", "whisper-tiny-onnx-trtrtx-validated");
        string wavePath = Path.GetFullPath(Path.Join(
            FindRepoRoot(), "tests", "Trackdub.Inference.Tests", "fixtures", "whisper-real-speech-16k-mono.wav"));
        Assert.True(File.Exists(wavePath), $"Speech fixture not found: {wavePath}");

        var regions = new[] { new SpeechRegion(0, 0.0, 6.0) };

        // Managed first: proves the graph-enabled session itself decodes correctly before the
        // bound path is compared against it.
        IReadOnlyList<RecognizedTranscriptSegment> managed;
        WhisperOnnxAudioTranscriptionEngine.TrtRtxBoundRunDisabled = true;
        try
        {
            var managedEngine = CreateEngine(stagingDir);
            managed = await managedEngine.TranscribeAsync(wavePath, regions, CancellationToken.None);
            Assert.False(
                WhisperOnnxAudioTranscriptionEngine.LastBoundTrtRtxRunUsed,
                "Test hook failed: managed run still reported bound-path usage.");
        }
        finally
        {
            WhisperOnnxAudioTranscriptionEngine.TrtRtxBoundRunDisabled = false;
        }

        string managedTranscript = string.Join(' ', managed.Select(static s => s.Text)).Trim();
        Assert.True(
            managedTranscript.Any(char.IsLetter),
            $"Managed TRT-RTX run produced no speech transcript: '{managedTranscript}'");

        var boundEngine = CreateEngine(stagingDir);
        IReadOnlyList<RecognizedTranscriptSegment> bound = await boundEngine.TranscribeAsync(
            wavePath, regions, CancellationToken.None);
        Assert.True(
            WhisperOnnxAudioTranscriptionEngine.LastBoundTrtRtxRunUsed,
            "Expected the TRT-RTX bound path to handle this model pair; it silently fell back to managed Run.");

        string boundTranscript = string.Join(' ', bound.Select(static s => s.Text)).Trim();
        Assert.True(
            boundTranscript.Any(char.IsLetter),
            $"Expected speech transcript from real-speech fixture, got: '{boundTranscript}'");

        Assert.Equal(
            managed.Select(static s => s.Text).ToArray(),
            bound.Select(static s => s.Text).ToArray());
    }

    private static async Task EnsureTrtRtxPluginRegisteredAsync()
    {
        TensorRtRtxBootstrapResult bootstrap = await TensorRtRtxPluginService.Shared
            .EnsureRegisteredAsync(allowProviderDownloads: false, CancellationToken.None);
        Assert.True(bootstrap.Succeeded, $"TRT-RTX plugin registration failed: {bootstrap.Detail}");
    }

    private static WhisperOnnxAudioTranscriptionEngine CreateEngine(string stagingDir) =>
        new(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Asr,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "onnx-community/whisper-tiny",
                ModelAlias = stagingDir,
                Variant = "default",
                ExecutionProvider = ExecutionProviderKind.TensorRTRtx
            }),
            new BenchmarkModelPathResolver());

    internal static string FindRepoRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "Trackdub.slnx")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string CreateSilenceWaveFile(double durationSeconds)
    {
        const int sampleRate = 48000;
        const int channels = 1;
        const int bitsPerSample = 16;
        int numSamples = (int)(sampleRate * durationSeconds);
        int dataSize = numSamples * channels * (bitsPerSample / 8);

        string path = Path.Join(Path.GetTempPath(), $"silence_{Guid.NewGuid():N}.wav");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * (bitsPerSample / 8));
        writer.Write((short)(channels * (bitsPerSample / 8)));
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8);
        writer.Write(dataSize);
        writer.Write(new byte[dataSize]);

        return path;
    }
}

file sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
{
    public Task<StageRuntimePlan> PlanAsync(StageRuntimePlanningRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(plan);
}
