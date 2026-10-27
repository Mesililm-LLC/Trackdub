using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Worker;
using Trackdub.Inference.Runtime.Planning;
using Xunit;

namespace Trackdub.Inference.Onnx.Tests.Worker;

public class KokoroWorkerHostTests
{
    private static StageRuntimePlan KokoroCudaPlan() =>
        new()
        {
            Stage = RuntimeStage.Tts,
            Status = StageRuntimePlanStatus.Ready,
            ModelId = "kokoro-model",
            ModelAlias = "kokoro-onnx",
            EngineFamily = "kokoro",
            Variant = "fp16",
            ExecutionProvider = ExecutionProviderKind.Cuda,
            ModelEntryPath = "/models/kokoro/model.onnx",
            ModelRootPath = "/models/kokoro",
        };

    private static WorkerTtsRequest SynthRequest(StageRuntimePlan plan) =>
        new(
            "Hello.",
            "en",
            new VoiceCatalogEntry("af_heart", "en-us", "female", "Heart"),
            1f,
            null,
            null,
            WorkerPlan.From(plan));

    [Fact]
    public async Task WarmThenSynthesize_ConstructsEngineOnce()
    {
        int constructions = 0;
        var engines = new List<FakeKokoroWorkerEngine>();
        using var host = new KokoroWorkerHost(() =>
        {
            constructions++;
            var engine = new FakeKokoroWorkerEngine();
            engines.Add(engine);
            return engine;
        });
        StageRuntimePlan plan = KokoroCudaPlan();

        WorkerWarmResult warm = await host.WarmAsync(WorkerPlan.From(plan), CancellationToken.None);
        WorkerTtsResult synth = await host.SynthesizeAsync(SynthRequest(plan), CancellationToken.None);

        Assert.Equal(1, constructions);
        Assert.Single(engines);
        Assert.Equal(1, engines[0].WarmCalls);
        Assert.Equal(1, engines[0].SynthesizeCalls);
        Assert.Equal("kokoro-model", warm.ModelId);
        Assert.Equal("cuda", warm.Provider);
        Assert.Equal("kokoro-model", synth.ModelId);
    }

    [Fact]
    public async Task SynthesizeWithoutWarm_StillWorksViaLazyConstruction()
    {
        int constructions = 0;
        using var host = new KokoroWorkerHost(() =>
        {
            constructions++;
            return new FakeKokoroWorkerEngine();
        });

        WorkerTtsResult synth = await host.SynthesizeAsync(
            SynthRequest(KokoroCudaPlan()), CancellationToken.None);

        Assert.Equal(1, constructions);
        Assert.Equal(2400, synth.DurationSamples);
    }

    [Fact]
    public async Task WarmFailure_ReportsReason_AndLaterSynthesizeRetriesInline()
    {
        var engine = new FakeKokoroWorkerEngine { WarmFailure = new InvalidOperationException("session create exploded") };
        using var host = new KokoroWorkerHost(() => engine);
        StageRuntimePlan plan = KokoroCudaPlan();

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.WarmAsync(WorkerPlan.From(plan), CancellationToken.None));
        Assert.Contains("session create exploded", ex.Message);

        // The failed warm must not poison the host: synthesis lazily retries construction.
        engine.WarmFailure = null;
        WorkerTtsResult synth = await host.SynthesizeAsync(SynthRequest(plan), CancellationToken.None);
        Assert.Equal(2400, synth.DurationSamples);
        Assert.Equal(1, engine.SynthesizeCalls);
    }

    [Fact]
    public async Task ReWarmAfterWorkerRestart_ConstructsAgain()
    {
        int constructions = 0;
        FakeKokoroWorkerEngine NewEngine()
        {
            constructions++;
            return new FakeKokoroWorkerEngine();
        }

        // First worker process lifetime.
        using (var first = new KokoroWorkerHost(NewEngine))
        {
            await first.WarmAsync(WorkerPlan.From(KokoroCudaPlan()), CancellationToken.None);
            await first.WarmAsync(WorkerPlan.From(KokoroCudaPlan()), CancellationToken.None);
        }

        Assert.Equal(1, constructions); // re-warm in one lifetime is a no-op construction-wise

        // Restarted worker: a new host must warm (construct) again, not assume readiness.
        using var restarted = new KokoroWorkerHost(NewEngine);
        await restarted.WarmAsync(WorkerPlan.From(KokoroCudaPlan()), CancellationToken.None);
        Assert.Equal(2, constructions);
    }

    [Fact]
    public async Task Warm_RejectsNonKokoroFamily()
    {
        using var host = new KokoroWorkerHost(() => new FakeKokoroWorkerEngine());
        StageRuntimePlan plan = KokoroCudaPlan();
        WorkerPlan workerPlan = WorkerPlan.From(plan) with { EngineFamily = "qwen3-tts" };

        await Assert.ThrowsAsync<NotSupportedException>(
            () => host.WarmAsync(workerPlan, CancellationToken.None));
    }

    [Fact]
    public async Task WarmProtocolMessage_RoundTripsThroughFraming()
    {
        StageRuntimePlan plan = KokoroCudaPlan();
        var request = new WorkerMessage(
            7,
            InferenceWorkerProtocol.TtsWarm,
            InferenceWorkerProtocol.ToPayload(new WorkerWarmRequest(WorkerPlan.From(plan))));
        using var stream = new MemoryStream();
        await InferenceWorkerProtocol.WriteAsync(stream, request, CancellationToken.None);
        stream.Position = 0;

        WorkerMessage? read = await InferenceWorkerProtocol.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(read);
        Assert.Equal(InferenceWorkerProtocol.TtsWarm, read!.Kind);
        WorkerWarmRequest payload = InferenceWorkerProtocol.FromPayload<WorkerWarmRequest>(read.Payload);
        Assert.Equal("kokoro", payload.Plan.EngineFamily);
        Assert.Equal(ExecutionProviderKind.Cuda, payload.Plan.ExecutionProvider);
        Assert.Equal(RuntimeStage.Tts, payload.Plan.Stage);
    }

    private sealed class FakeKokoroWorkerEngine : IKokoroWorkerEngine
    {
        public int WarmCalls { get; private set; }
        public int SynthesizeCalls { get; private set; }
        public Exception? WarmFailure { get; set; }
        public bool Disposed { get; private set; }

        public StageRuntimeExecutionSummary? LastExecutionSummary =>
            new("cuda", "cuda", "kokoro-model", "kokoro-onnx", "fp16", null);

        public Task<StageRuntimeExecutionSummary> WarmAsync(
            StageRuntimePlan plan, CancellationToken cancellationToken)
        {
            WarmCalls++;
            return WarmFailure is not null
                ? Task.FromException<StageRuntimeExecutionSummary>(WarmFailure)
                : Task.FromResult(LastExecutionSummary!);
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request, StageRuntimePlan plan, CancellationToken cancellationToken)
        {
            SynthesizeCalls++;
            return Task.FromResult(new TtsSynthesisResult(
                WavBytes: [1, 2, 3],
                DurationSamples: 2400,
                SampleRate: 24000,
                ModelId: plan.ModelId ?? "kokoro-model",
                VoiceId: request.Voice.VoiceId,
                Provider: "cuda"));
        }

        public void Dispose() => Disposed = true;
    }
}
