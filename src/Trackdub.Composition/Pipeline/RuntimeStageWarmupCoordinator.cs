using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Licensing;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.Worker;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Warms exactly the model the planner would select for an imminent runtime stage by running
/// the real plan + provider smoke profile ahead of execution. The smoke path reuses pooled
/// ONNX sessions and shared GenAI models, so the next stage's first lease is warm. Purely
/// optimization: no stage-run records, artifacts, or readiness state are produced.
/// </summary>
public sealed class RuntimeStageWarmupCoordinator(
    IRuntimePlanner runtimePlanner,
    IExecutionProviderSmokeTester executionProviderSmokeTester,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IInferenceWorkerClient? inferenceWorkerClient = null,
    Func<bool>? hostCudaProviderListed = null)
    : IStageWarmupCoordinator
{
    public async Task<StageWarmupResult> WarmAsync(
        StageWarmupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        BenchmarkPhaseCapture.Increment("stageWarmupAttempt");

        try
        {
            using (BenchmarkPhaseCapture.Start("stage-warmup"))
            {
                RuntimeModelRequestOptions options =
                    RuntimeModelRequestFactory.CreateOptions(request.Selections);
                RuntimeModelRequest modelRequest = request.Stage switch
                {
                    RuntimeStage.Asr => RuntimeModelRequestFactory.CreateAsrRequest(
                        options, request.SourceLanguageCode),
                    RuntimeStage.Translation => RuntimeModelRequestFactory.CreateTranslationRequest(
                        options,
                        request.SourceLanguageCode ?? string.Empty,
                        request.TargetLanguageCode ?? string.Empty),
                    RuntimeStage.Tts => RuntimeModelRequestFactory.CreateTtsRequest(
                        options, request.RequiresVoiceClone),
                    RuntimeStage.Diarization => RuntimeModelRequestFactory.CreateDiarizationRequest(options),
                    RuntimeStage.OverlapRescue => RuntimeModelRequestFactory.CreateOverlapRescueRequest(options),
                    RuntimeStage.TextRefinement => RuntimeModelRequestFactory.CreateTextRefinementRequest(options),
                    RuntimeStage.LipSync => RuntimeModelRequestFactory.CreateLipSyncRequest(options),
                    RuntimeStage.LipSynthesis => RuntimeModelRequestFactory.CreateLipSynthesisRequest(options),
                    _ => RuntimeModelRequestFactory.CreateStageRequest(options, request.Stage),
                };

                StageRuntimePlanningRequest planningRequest =
                    await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
                        new StageRuntimePlanningRequest(
                            modelRequest.Stage,
                            PreferredModelAlias: modelRequest.PreferredModelAlias,
                            SourceLanguage: modelRequest.SourceLanguage,
                            TargetLanguage: modelRequest.TargetLanguage,
                            RequirePreferredModelAlias: modelRequest.RequirePreferredModelAlias,
                            PreferredExecutionProvider: modelRequest.PreferredExecutionProvider,
                            RequirePreferredExecutionProvider: modelRequest.RequirePreferredExecutionProvider,
                            PreferredModelVariantAlias: modelRequest.PreferredModelVariantAlias),
                        runtimePlanningPreferences,
                        cancellationToken).ConfigureAwait(false);

                StageRuntimePlan plan = await runtimePlanner
                    .PlanAsync(planningRequest, cancellationToken)
                    .ConfigureAwait(false);

                if (!plan.IsRunnable()
                    || plan.ExecutionProvider is not { } provider
                    || string.IsNullOrWhiteSpace(plan.ModelId)
                    || string.IsNullOrWhiteSpace(plan.ModelAlias)
                    || string.IsNullOrWhiteSpace(plan.Variant)
                    || string.IsNullOrWhiteSpace(plan.ModelRootPath)
                    || string.IsNullOrWhiteSpace(plan.ModelEntryPath))
                {
                    BenchmarkPhaseCapture.Increment("stageWarmupFailure");
                    return new StageWarmupResult(
                        Attempted: false,
                        Succeeded: false,
                        Detail: $"Stage '{request.Stage}' has no runnable plan to warm "
                            + $"(status {plan.Status}, detail {plan.Fallback?.Detail ?? "none"}).");
                }

                // Worker-backed CUDA TTS pays worker spawn + handshake + Kokoro session
                // construction on its first synthesis. Warm it here — this coordinator is
                // the background path (the pipeline only joins the warmup before the TTS
                // stage), so planning never blocks on it. A warm failure reports its
                // reason and skips the smoke: stage execution still lazy-constructs
                // inline, so no readiness is faked or lost.
                if (IsWorkerBackedCudaTts(plan, provider))
                {
                    try
                    {
                        WorkerWarmResult warm = await inferenceWorkerClient!
                            .WarmTtsAsync(plan, cancellationToken)
                            .ConfigureAwait(false);
                        BenchmarkPhaseCapture.Increment("workerTtsWarmSuccess");
                        _ = warm;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        BenchmarkPhaseCapture.Increment("workerTtsWarmFailure");
                        BenchmarkPhaseCapture.Increment("stageWarmupFailure");
                        return new StageWarmupResult(
                            Attempted: true,
                            Succeeded: false,
                            Detail: $"Inference worker warm failed: {ex.Message} " +
                                "The TTS stage will retry construction inline.");
                    }
                }

                // The smoke profile is the actual per-run warmup: it exercises provider
                // registration plus the pooled session/GenAI load path, so a persisted
                // planner verdict must not skip it.
                ExecutionProviderSmokeTestResult smoke = await executionProviderSmokeTester
                    .SmokeTestAsync(
                        new ExecutionProviderSmokeTestRequest(
                            plan.Stage,
                            plan.ModelId,
                            plan.ModelAlias,
                            plan.EngineFamily,
                            plan.Variant,
                            provider,
                            plan.ModelRootPath,
                            plan.ModelEntryPath,
                            plan.ModelRevisionHash),
                        cancellationToken)
                    .ConfigureAwait(false);

                BenchmarkPhaseCapture.Increment(
                    smoke.Passed ? "stageWarmupSuccess" : "stageWarmupFailure");
                return new StageWarmupResult(
                    Attempted: true,
                    Succeeded: smoke.Passed,
                    Detail: smoke.Passed ? null : smoke.Detail);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            BenchmarkPhaseCapture.Increment("stageWarmupFailure");
            throw;
        }
        catch (Exception ex)
        {
            // Optimization must never fail the run.
            BenchmarkPhaseCapture.Increment("stageWarmupFailure");
            return new StageWarmupResult(Attempted: true, Succeeded: false, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Mirrors <see cref="Trackdub.Inference.Onnx.Kokoro.KokoroTtsEngine"/>'s worker routing:
    /// a CUDA TTS plan for a worker-hosted family while this process cannot list CUDA itself.
    /// </summary>
    private bool IsWorkerBackedCudaTts(StageRuntimePlan plan, ExecutionProviderKind provider) =>
        inferenceWorkerClient is { IsInstalled: true } &&
        plan.Stage is RuntimeStage.Tts &&
        provider is ExecutionProviderKind.Cuda &&
        plan.EngineFamily is not null &&
        OnnxExecutionProviderDiscovery.WorkerCudaEngineFamilies.Contains(
            plan.EngineFamily, StringComparer.OrdinalIgnoreCase) &&
        !(hostCudaProviderListed?.Invoke() ?? CudaOrtProbe.IsCudaProviderListed());
}
