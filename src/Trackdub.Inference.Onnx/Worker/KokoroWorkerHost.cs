using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Worker;

/// <summary>
/// The slice of <see cref="KokoroTtsEngine"/> the inference worker hosts. Warm creates the
/// real session without synthesis; synthesis keeps its lazy construction when no warm ran.
/// </summary>
internal interface IKokoroWorkerEngine : IDisposable
{
    Task<StageRuntimeExecutionSummary> WarmAsync(StageRuntimePlan plan, CancellationToken cancellationToken);

    Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        StageRuntimePlan plan,
        CancellationToken cancellationToken);

    StageRuntimeExecutionSummary? LastExecutionSummary { get; }
}

/// <summary>
/// Owns the worker process's single Kokoro engine: constructed at most once per worker
/// lifetime (a restarted worker is a new host and must warm again), never cached as a
/// failure — a failed warm leaves the engine usable for the lazy inline retry on the
/// next synthesis, mirroring the non-caching construction in the worker entry point.
/// </summary>
internal sealed class KokoroWorkerHost(Func<IKokoroWorkerEngine> engineFactory) : IDisposable
{
    private readonly Func<IKokoroWorkerEngine> engineFactory =
        engineFactory ?? throw new ArgumentNullException(nameof(engineFactory));
    private readonly object gate = new();
    private IKokoroWorkerEngine? engine;
    private bool disposed;

    public async Task<WorkerWarmResult> WarmAsync(WorkerPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        StageRuntimePlan stagePlan = RequireKokoroPlan(plan);
        StageRuntimeExecutionSummary summary = await GetEngine()
            .WarmAsync(stagePlan, cancellationToken)
            .ConfigureAwait(false);
        return new WorkerWarmResult(plan.ModelId, summary.SelectedProvider, summary.BootstrapDetail);
    }

    public async Task<WorkerTtsResult> SynthesizeAsync(WorkerTtsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        StageRuntimePlan plan = RequireKokoroPlan(request.Plan);
        IKokoroWorkerEngine host = GetEngine();
        TtsSynthesisResult result = await host.SynthesizeAsync(
            new TtsSynthesisRequest(
                request.Text,
                request.LanguageCode,
                request.Voice,
                request.Speed,
                request.PhonemeOverride,
                TargetDurationSeconds: request.TargetDurationSeconds),
            plan,
            cancellationToken).ConfigureAwait(false);
        return new WorkerTtsResult(
            Convert.ToBase64String(result.WavBytes),
            result.DurationSamples,
            result.SampleRate,
            result.ModelId,
            result.VoiceId,
            result.Provider,
            host.LastExecutionSummary);
    }

    private IKokoroWorkerEngine GetEngine()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return engine ??= engineFactory();
        }
    }

    private static StageRuntimePlan RequireKokoroPlan(WorkerPlan plan)
    {
        if (!string.Equals(plan.EngineFamily, KokoroTtsEngine.EngineFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"The inference worker does not host TTS engine family '{plan.EngineFamily}'.");
        }

        return plan.ToStageRuntimePlan();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            engine?.Dispose();
            engine = null;
        }
    }
}
