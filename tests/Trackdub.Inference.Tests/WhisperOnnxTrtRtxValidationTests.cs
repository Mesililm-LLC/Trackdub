using System.Diagnostics;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
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
/// Runs the real large-v3 OOM probe in a separate process. The probe is supplied by the
/// hardware-validation tooling because only that process can safely provoke the device OOM.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresTrtRtxOomProbeFactAttribute : FactAttribute
{
    public RequiresTrtRtxOomProbeFactAttribute()
    {
        string? repoRoot = TestRepoRootResolver.TryFindRepoRoot();
        string? executable = Environment.GetEnvironmentVariable("TRACKDUB_TRT_RTX_OOM_PROBE_EXE");
        string staging = repoRoot is null
            ? string.Empty
            : Path.Join(repoRoot, "build", "whisper-large-v3-onnx-trtrtx-validated");
        if (repoRoot is null || !Directory.Exists(staging) || string.IsNullOrWhiteSpace(executable) ||
            !File.Exists(executable))
        {
            Skip = "Requires the validated whisper-large-v3 TRT-RTX artifact, hardware, and " +
                   "TRACKDUB_TRT_RTX_OOM_PROBE_EXE (a probe that loads the encoder, triggers " +
                   "decoder CUDA OOM, disposes the lease, and returns 0).";
        }
    }
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

    [RequiresTrtRtxOomProbeFact]
    public async Task WhisperOnnxTrtRtx_LargeV3DecoderOutOfMemoryChildSurvivesTeardown()
    {
        string stagingDir = Path.Join(FindRepoRoot(), "build", "whisper-large-v3-onnx-trtrtx-validated");
        string executable = Environment.GetEnvironmentVariable("TRACKDUB_TRT_RTX_OOM_PROBE_EXE")!;
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(stagingDir);
        startInfo.ArgumentList.Add("--trigger-decoder-oom");

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException("The TRT-RTX OOM teardown child exceeded five minutes.");
        }

        Assert.True(process.ExitCode == 0,
            $"TRT-RTX OOM child crashed or failed ({process.ExitCode}).\n{await stdout}\n{await stderr}");
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
