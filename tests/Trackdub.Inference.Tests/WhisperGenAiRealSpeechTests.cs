using System.Runtime.InteropServices;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Whisper;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Real-speech parity tests for <see cref="WhisperGenAiAudioTranscriptionEngine"/>, closing the
/// gap called out in <c>docs/audits/c13-trt-rtx-shape-triage</c> follow-up work: the existing
/// bundled-model test (<c>RunsBundledModelOnSilence</c>) only proves the engine runs on silence,
/// while these tests prove it transcribes actual speech content and that repeated runs agree
/// with each other (transcript determinism, the ASR analog of the Phi GenAI stream-parity suite).
/// </summary>
public sealed class WhisperGenAiRealSpeechTests
{
    /// <summary>
    /// Six seconds of real Spanish speech (16 kHz mono PCM16), extracted from the batch smoke
    /// source media. Mean volume -16.5 dB, no silence gaps longer than 0.3 s at -30 dB, so the
    /// single region below is genuinely speech-bearing.
    /// </summary>
    private const string FixtureRelativePath = "fixtures/whisper-real-speech-16k-mono.wav";

    [RequiresBundledModelFact(
        "whisper-tiny-genai/genai_config.json",
        "whisper-tiny-genai/audio_processor_config.json",
        "whisper-tiny-genai/encoder.onnx",
        "whisper-tiny-genai/decoder.onnx",
        "whisper-tiny-genai/encoder.onnx.data",
        "whisper-tiny-genai/decoder.onnx.data")]
    [Trait("Category", "Integration")]
    public async Task WhisperGenAi_TranscribesRealEnglishSpeech()
    {
        string wavePath = ResolveFixturePath();
        var engine = CreateEngine();

        IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
            wavePath,
            [new SpeechRegion(0, 0.0, 6.0)],
            CancellationToken.None);

        Assert.NotEmpty(segments);
        string transcript = string.Join(' ', segments.Select(static segment => segment.Text)).Trim();
        Assert.False(
            string.IsNullOrWhiteSpace(transcript),
            "The engine produced no transcript for real speech audio.");
        // Real-speech guard, mirroring the engine's own degenerate-text classifier: a transcript
        // of only punctuation/whitespace (the classic whisper hallucination on noise) must fail.
        Assert.True(transcript.Any(char.IsLetter), "Transcript must contain at least one letter to be valid speech output.");
        Assert.All(segments, static segment => Assert.InRange(segment.EndSeconds, segment.StartSeconds, 6.0 + 0.01));
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("whisper-tiny-genai", engine.LastExecutionSummary.ModelAlias);
    }

    [RequiresBundledModelFact(
        "whisper-tiny-genai/genai_config.json",
        "whisper-tiny-genai/audio_processor_config.json",
        "whisper-tiny-genai/encoder.onnx",
        "whisper-tiny-genai/decoder.onnx",
        "whisper-tiny-genai/encoder.onnx.data",
        "whisper-tiny-genai/decoder.onnx.data")]
    [Trait("Category", "Integration")]
    public async Task WhisperGenAi_RealSpeechTranscript_IsDeterministicAcrossRuns()
    {
        string wavePath = ResolveFixturePath();

        var firstEngine = CreateEngine();
        var secondEngine = CreateEngine();

        IReadOnlyList<RecognizedTranscriptSegment> first =
            await firstEngine.TranscribeAsync(wavePath, [new SpeechRegion(0, 0.0, 6.0)], CancellationToken.None);
        IReadOnlyList<RecognizedTranscriptSegment> second =
            await secondEngine.TranscribeAsync(wavePath, [new SpeechRegion(0, 0.0, 6.0)], CancellationToken.None);

        // Parity between two runs on the same audio: same segment count, same per-segment
        // text, same timings. GenAI decoding here is beam search (num_beams=5) with no
        // sampling, so a mismatch means the engine introduced nondeterminism (state leaking
        // through the shared pooled model, or chunk boundary drift), which is what the D1
        // streaming work needs to rule out.
        Assert.NotEmpty(first);
        Assert.NotEmpty(second);
        Assert.Equal(
            first.Select(static segment => (segment.Index, segment.StartSeconds, segment.EndSeconds, segment.Text)),
            second.Select(static segment => (segment.Index, segment.StartSeconds, segment.EndSeconds, segment.Text)));
    }

    [RequiresBundledModelFact(
        "whisper-tiny-genai/genai_config.json",
        "whisper-tiny-genai/audio_processor_config.json",
        "whisper-tiny-genai/encoder.onnx",
        "whisper-tiny-genai/decoder.onnx",
        "whisper-tiny-genai/encoder.onnx.data",
        "whisper-tiny-genai/decoder.onnx.data")]
    [Trait("Category", "Integration")]
    public async Task WhisperGenAi_AutoLanguageContext_PreservesSeparateRegionIdentities()
    {
        SpeechRegion[] regions = [new(7, 0.0, 3.0), new(42, 3.0, 6.0)];
        var engine = CreateEngine();

        IReadOnlyList<RecognizedTranscriptSegment> segments = await engine.TranscribeAsync(
            ResolveFixturePath(), regions, CancellationToken.None);

        // Language detection may combine these windows; transcription must not combine them
        // or replace their indices, which are used to assign speakers and update selected rows.
        Assert.Equal(regions.Select(region => region.Index), segments.Select(segment => segment.Index));
        for (int index = 0; index < regions.Length; index++)
        {
            Assert.Equal(regions[index].StartSeconds, segments[index].StartSeconds);
            Assert.Equal(regions[index].EndSeconds, segments[index].EndSeconds);
            Assert.Equal("es", segments[index].DetectedLanguage);
            Assert.Contains(segments[index].Text, char.IsLetter);
        }
    }

    private static WhisperGenAiAudioTranscriptionEngine CreateEngine() =>
        new(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.Asr,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "openai/whisper-tiny",
                ModelAlias = "whisper-tiny-genai",
                Variant = "default",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

    private static string ResolveFixturePath() =>
        Path.GetFullPath(Path.Join(
            TestRepoRootResolver.FindRepoRoot(),
            "tests",
            "Trackdub.Inference.Tests",
            FixtureRelativePath));

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan with
            {
                Stage = request.Stage
            });
    }
}
