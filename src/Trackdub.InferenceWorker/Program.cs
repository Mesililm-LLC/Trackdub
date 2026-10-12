using System.Collections.Concurrent;
using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.Kokoro;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.Worker;
using Trackdub.Inference.Runtime.Planning;

// ORT 1.30 inference worker (ADR-0017). Standard output carries protocol messages only; all
// diagnostics go to standard error, which the application keeps as the crash tail.
Stream input = Console.OpenStandardInput();
Stream output = Console.OpenStandardOutput();

// Opt-in EcoQoS (MS efficiency guidance, CPU workloads): the worker is a background helper
// process, so operators may prefer efficiency-oriented CPU scheduling for it. The variable is
// inherited from the host process env; default stays OS-managed. The outcome is verified by
// read-back and reported — never claimed without proof.
if (string.Equals(
        Environment.GetEnvironmentVariable(Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.EnvironmentVariable),
        "1", StringComparison.Ordinal))
{
    (bool ecoQosApplied, string? ecoQosError) =
        Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.EnableExecutionSpeedThrottling();
    Console.Error.WriteLine(ecoQosApplied
        ? $"[worker] EcoQoS enabled (verified: {Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.IsExecutionSpeedThrottled})."
        : $"[worker] EcoQoS requested but NOT applied: {ecoQosError}");
}
var writeGate = new SemaphoreSlim(1, 1);
var inFlight = new ConcurrentDictionary<long, CancellationTokenSource>();
var handlers = new ConcurrentDictionary<long, Task>();
// Created on first use but never cached as a failure, unlike Lazy<T>: a transient construction
// error (an unreadable manifest, a model cache still being written) must not poison every later
// request for the life of the worker.
var kokoroGate = new object();
KokoroTtsEngine? kokoro = null;

KokoroTtsEngine GetKokoro()
{
    lock (kokoroGate)
    {
        return kokoro ??= new KokoroTtsEngine(
            new PlanSuppliedByApplication(),
            BenchmarkModelPathResolver.CreateDefault(),
            new EspeakNgPhonemizer());
    }
}

async Task RespondAsync(WorkerMessage message)
{
    await writeGate.WaitAsync().ConfigureAwait(false);
    try
    {
        await InferenceWorkerProtocol.WriteAsync(output, message, CancellationToken.None).ConfigureAwait(false);
    }
    finally
    {
        writeGate.Release();
    }
}

async Task HandleAsync(WorkerMessage request, CancellationToken cancellationToken)
{
    try
    {
        WorkerMessage response = request.Kind switch
        {
            InferenceWorkerProtocol.Hello => new WorkerMessage(request.Id, InferenceWorkerProtocol.Result,
                InferenceWorkerProtocol.ToPayload(DescribeWorker())),
            InferenceWorkerProtocol.TtsSynthesize => new WorkerMessage(request.Id, InferenceWorkerProtocol.Result,
                InferenceWorkerProtocol.ToPayload(await SynthesizeAsync(
                    InferenceWorkerProtocol.FromPayload<WorkerTtsRequest>(request.Payload),
                    cancellationToken).ConfigureAwait(false))),
            _ => throw new NotSupportedException($"Unknown inference worker request '{request.Kind}'."),
        };
        await RespondAsync(response).ConfigureAwait(false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[worker] {request.Kind} #{request.Id} failed: {ex}");
        await RespondAsync(new WorkerMessage(request.Id, InferenceWorkerProtocol.Error,
            Error: ex.Message, ErrorType: ex is OperationCanceledException ? nameof(OperationCanceledException) : ex.GetType().Name))
            .ConfigureAwait(false);
    }
    finally
    {
        if (inFlight.TryRemove(request.Id, out CancellationTokenSource? source))
        {
            source.Dispose();
        }
    }
}

async Task<WorkerTtsResult> SynthesizeAsync(WorkerTtsRequest request, CancellationToken cancellationToken)
{
    StageRuntimePlan plan = request.Plan.ToStageRuntimePlan();
    KokoroTtsEngine engine = string.Equals(plan.EngineFamily, KokoroTtsEngine.EngineFamilyName, StringComparison.OrdinalIgnoreCase)
        ? GetKokoro()
        : throw new NotSupportedException($"The inference worker does not host TTS engine family '{plan.EngineFamily}'.");
    TtsSynthesisResult result = await engine.SynthesizeAsync(
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
        engine.LastExecutionSummary);
}

static WorkerHelloResult DescribeWorker()
{
    bool cudaListed = CudaOrtProbe.IsCudaProviderListed();
    bool cuDnn = CudaOrtProbe.IsCuDnn9Available();
    string detail = (cudaListed, cuDnn) switch
    {
        (true, true) => "Native ORT CUDA execution provider and cuDNN 9 are available.",
        (false, _) => "This worker's ONNX Runtime build does not list CUDAExecutionProvider.",
        (true, false) => "cuDNN 9 (cudnn64_9.dll) for CUDA 13 was not found on PATH.",
    };
    return new WorkerHelloResult(
        InferenceWorkerProtocol.Version,
        InferenceWorkerProtocol.BuildStamp,
        OrtEnv.Instance().GetVersionString(),
        cudaListed && cuDnn,
        detail);
}

async Task CancelQuietlyAsync(CancellationTokenSource source)
{
    try
    {
        await source.CancelAsync().ConfigureAwait(false);
    }
    catch (ObjectDisposedException)
    {
        // The request finished and disposed its source between lookup and cancel.
    }
}

// Cancel what is still running, let it finish (its sessions are released by the engine), then
// release the engine's native ONNX Runtime sessions before the process exits.
async Task DrainAsync()
{
    foreach (CancellationTokenSource source in inFlight.Values)
    {
        await CancelQuietlyAsync(source).ConfigureAwait(false);
    }

    try
    {
        await Task.WhenAll(handlers.Values).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
    }
    catch (TimeoutException)
    {
        Console.Error.WriteLine("[worker] In-flight requests did not stop within 10 s; exiting anyway.");
        return;
    }
    catch (Exception ex)
    {
        // Handlers report their own failures; a broken pipe while answering must not skip cleanup.
        Console.Error.WriteLine($"[worker] An in-flight request failed during shutdown: {ex.Message}");
    }

    lock (kokoroGate)
    {
        kokoro?.Dispose();
    }
}

while (await InferenceWorkerProtocol.ReadAsync(input, CancellationToken.None).ConfigureAwait(false) is { } message)
{
    switch (message.Kind)
    {
        case InferenceWorkerProtocol.Shutdown:
            await DrainAsync().ConfigureAwait(false);
            return 0;
        case InferenceWorkerProtocol.Cancel:
            long target = InferenceWorkerProtocol.FromPayload<long>(message.Payload);
            if (inFlight.TryGetValue(target, out CancellationTokenSource? running))
            {
                await CancelQuietlyAsync(running).ConfigureAwait(false);
            }

            break;
        default:
            var cancellation = new CancellationTokenSource();
            inFlight[message.Id] = cancellation;
            Task handler = Task.Run(() => HandleAsync(message, cancellation.Token));
            handlers[message.Id] = handler;
            _ = handler.ContinueWith(done => handlers.TryRemove(message.Id, out _), TaskScheduler.Default);
            break;
    }
}

// The application closed the pipe without a shutdown message.
await DrainAsync().ConfigureAwait(false);
return 0;

/// <summary>The worker never plans: the application's planner already chose model and provider.</summary>
internal sealed class PlanSuppliedByApplication : IRuntimePlanner
{
    public Task<StageRuntimePlan> PlanAsync(StageRuntimePlanningRequest request, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The inference worker only runs plans supplied by the application.");
}
