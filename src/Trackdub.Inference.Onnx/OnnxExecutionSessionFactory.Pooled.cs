using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.EpContext;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.Pool;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.Migraphx;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Trackdub.Inference.Runtime.WinMlCatalog;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx;

internal static partial class OnnxExecutionSessionFactory
{
    public static async Task WarmPooledSingleAsync(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool allowTrtInitFallback = true)
    {
        using SingleSessionLease _ = await CreatePooledSingleAsync(
            engineFamily,
            modelPath,
            provider,
            cancellationToken,
            additionalTrtOptions: additionalTrtOptions,
            allowTrtInitFallback: allowTrtInitFallback).ConfigureAwait(false);
    }

    public static async Task<SingleSessionLease> CreatePooledSingleAsync(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null,
        bool allowTrtInitFallback = true)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await BootstrapTrackedAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection optionsSelection = CreateSessionOptions(new SessionOptionsRequest(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtOptions,
            TrtModelPath: modelPath));

        // Options ownership may transfer to a replacement SessionOptionsSelection when TRT init
        // falls back; keep a movable dispose target managed by a `using` scope.
        using var optionsHolder = new SessionOptionsDisposeHolder(optionsSelection.Options);
        SessionOptionsSelection leaseSelection = optionsSelection;
        bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, optionsSelection.SelectedProvider);
        ExecutionProviderKind optionsSelectedProvider = optionsSelection.SelectedProvider;

        string optionsFingerprint =
            $"{BuildSessionOptionsFingerprint(optionsSelectedProvider, devicePolicy, additionalTrtOptions)}|trt-init-fallback:{allowTrtInitFallback}";
        SessionPoolKey key = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            engineFamily,
            modelId,
            variant,
            optionsSelection.SelectedProvider,
            modelPath,
            deviceId: null,
            graphRole: "default",
            optionsFingerprint,
            cancellationToken).ConfigureAwait(false));
        if (allowTrtInitFallback && optionsSelection.SelectedProvider is ExecutionProviderKind.TensorRTRtx)
        {
            // TRT RTX init can still fall back to DirectML/CPU inside the pool factory, after this
            // key is admitted; reserve for the fallback provider, not TRT RTX's measured factor.
            key = key with
            {
                EstimatedVramMb = SessionPoolKey.EstimateVramMb(
                    modelPath, optionsSelection.SelectedProvider, providerMayFallBack: true),
            };
        }

        SessionLease? poolLease = null;
        try
        {
            // The key is admitted for the pre-creation provider; reporting the provider the
            // fallback actually created the session with lets the pool move a CPU fallback's
            // accounting from the device budget to host RAM.
            poolLease = await pool
                .GetLeaseReportingProviderAsync(
                    key,
                    ct =>
                    {
                        (InferenceSession session, SessionOptionsSelection selection) =
                            CreateInferenceSessionWithTrtInitFallback(
                                modelPath,
                                optionsSelection,
                                devicePolicy,
                                additionalTrtOptions,
                                sessionFactory,
                                ct,
                                allowTrtInitFallback);
                        leaseSelection = selection;
                        optionsSelectedProvider = selection.SelectedProvider;
                        useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, selection.SelectedProvider);
                        if (!ReferenceEquals(selection.Options, optionsHolder.Current))
                        {
                            optionsHolder.Current = selection.Options;
                        }

                        // Report where ORT actually placed the graph: a device EP that initialised
                        // but left every node on the CPU must be charged to host RAM.
                        ExecutionProviderKind placed = ResolveEffectiveProviderKindFromSession(
                            session,
                            selection.SelectedProvider,
                            useCatalogDevicePolicy);
                        TensorRtRtxTeardownGuard.ReconcileTracking(session, placed);
                        return Task.FromResult(new CreatedPoolSession(session, placed));
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            ExecutionProviderKind effectiveProvider = ResolveEffectiveProviderKindFromSession(
                poolLease.Session,
                optionsSelectedProvider,
                useCatalogDevicePolicy);
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? epFallbackReason = BuildSessionOptionsFallbackReason(provider, effectiveProvider, leaseSelection);
            string? bootstrapDetail = FormatBootstrapDetail(bootstrapResult.Detail, epFallbackReason);
            CpuExecutionAdmission.Shared.RegisterSession(poolLease.Session, effectiveProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(poolLease.Session, effectiveProvider);

            return new SingleSessionLease(poolLease.Session, requestedProvider, selectedProvider, bootstrapDetail)
            {
                PoolLease = poolLease,
                ResolvedPoolKey = key,
            };
        }
        catch
        {
            poolLease?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Pins a pooled single session for engine residency (audit §3A) without holding the
    /// execution lease between calls. Use <see cref="PooledSingleSessionPin.AcquireAsync"/>
    /// for a short exclusive lease around each Run().
    /// </summary>
    public static async Task<PooledSingleSessionPin> PinPooledSingleAsync(
        string engineFamily,
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null,
        bool allowTrtInitFallback = true)
    {
        pool ??= InferenceSessionPool.Shared;

        SessionPoolKey? key = null;
        string requestedProvider = FormatProviderLabel(provider);
        string selectedProvider = requestedProvider;
        string? bootstrapDetail = null;

        async Task<SingleSessionLease> AcquireAsync(CancellationToken ct)
        {
            SingleSessionLease lease = await CreatePooledSingleAsync(
                engineFamily,
                modelPath,
                provider,
                ct,
                pool,
                modelId,
                variant,
                additionalTrtOptions,
                sessionFactory,
                allowTrtInitFallback).ConfigureAwait(false);
            if (key is null)
            {
                key = lease.ResolvedPoolKey;
                selectedProvider = lease.SelectedProvider;
                bootstrapDetail = lease.BootstrapDetail;
            }

            return lease;
        }

        // Establish the session and capture the pool key / provider metadata.
        using (await AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
        }

        SessionPoolKey resolvedKey = key
            ?? throw new InvalidOperationException("Pooled single session did not resolve a pool key.");

        // Bound retries: when the pool is full of leased/pinned entries, GetLeaseAsync
        // returns an ephemeral lease that never reaches `entries`, so TryPinExisting
        // can never succeed — infinite session create/dispose churn.
        const int maxPinAttempts = 5;
        for (int attempt = 1; attempt <= maxPinAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pool.TryPinExisting(resolvedKey, out SessionResidency? pinned) && pinned is not null)
            {
                return new PooledSingleSessionPin(pinned, AcquireAsync, requestedProvider, selectedProvider, bootstrapDetail);
            }

            if (attempt == maxPinAttempts)
            {
                break;
            }

            // Evicted before pin — recreate and retry with exponential backoff.
            // Retry delays: 50ms, 100ms, 200ms, 400ms (maxPinAttempts=5).
            int delayMs = 25 * (1 << attempt);
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);

            using (await AcquireAsync(cancellationToken).ConfigureAwait(false))
            {
            }
        }

        // Pool stayed at capacity (leased/pinned) across every retry: fall back to no
        // residency instead of failing engine init. Each AcquireAsync call still creates a
        // short, working (possibly ephemeral) lease — callers just lose the "stay resident
        // between calls" guarantee until pool pressure eases.
        return new PooledSingleSessionPin(
            new SessionResidency(static () => { }), AcquireAsync, requestedProvider, selectedProvider, bootstrapDetail);
    }

    public static async Task<WhisperSessionLease> CreatePooledWhisperAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null,
        bool allowTrtInitFallback = true)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        ExecutionProviderKind selectedProviderKind = DowngradeTrtRtxForUnsupportedGraphs(
            provider,
            [encoderModelPath, decoderModelPath],
            out string? graphFallbackReason);

        // TensorRT RTX can fail a Whisper session at engine build or load (CUDA OOM, importer
        // errors) after the pre-flight passed. Walk the same DirectML (Windows) then CPU chain the
        // single-session path uses, recording why each provider was abandoned.
        List<ExecutionProviderKind> attempts = [selectedProviderKind];
        if (allowTrtInitFallback && selectedProviderKind is ExecutionProviderKind.TensorRTRtx)
        {
            attempts.AddRange(EnumerateTrtInitFallbackProviders());
        }

        string? abandonedReason = null;
        for (int attempt = 0; ; attempt++)
        {
            ExecutionProviderKind attemptProvider = attempts[attempt];
            try
            {
                DualPooledLeasePair pair = await AcquirePooledWhisperPairAsync(
                    engineFamily, encoderModelPath, decoderModelPath, attemptProvider, cancellationToken,
                    pool, modelId, variant, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
                    sessionFactory).ConfigureAwait(false);

                return new WhisperSessionLease(
                    pair.EncoderLease.Session, pair.DecoderLease.Session,
                    requestedProvider, FormatProviderLabel(pair.SelectedProvider),
                    MergeFallbackReasons(MergeFallbackReasons(graphFallbackReason, abandonedReason), pair.BootstrapDetail))
                {
                    EncoderPoolLease = pair.EncoderLease,
                    DecoderPoolLease = pair.DecoderLease
                };
            }
            catch (Exception ex)
            {
                if (attemptProvider is ExecutionProviderKind.TensorRTRtx)
                {
                    TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(ex);
                }

                if (attempt < attempts.Count - 1
                    && !cancellationToken.IsCancellationRequested
                    && IsRecoverableTrtFallbackInitFailure(ex))
                {
                    try
                    {
                        await pool.EvictModelAsync(engineFamily, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception evictionFailure) when (evictionFailure is not OperationCanceledException)
                    {
                        // A failed eviction must not abort the fallback chain or replace the init failure.
                        _logger?.LogWarning(
                            evictionFailure,
                            "Evicting {EngineFamily} after a failed {Provider} Whisper init failed; continuing fallback.",
                            engineFamily,
                            FormatProviderLabel(attemptProvider));
                    }
                    abandonedReason = MergeFallbackReasons(
                        abandonedReason,
                        $"{FormatProviderLabel(attemptProvider)} Whisper session init failed ({SummarizeExceptionMessage(ex)}); "
                        + $"fell back to {FormatProviderLabel(attempts[attempt + 1])}.");
                    continue;
                }

                throw;
            }
        }
    }

    private static async Task<DualPooledLeasePair> AcquirePooledWhisperPairAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool pool,
        string? modelId,
        string? variant,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions,
        Func<string, SessionOptions, InferenceSession>? sessionFactory)
    {
        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            encoderLogId: ExtractLogId(encoderModelPath),
            decoderLogId: ExtractLogId(decoderModelPath),
            encoderModelPath: encoderModelPath, decoderModelPath: decoderModelPath);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        (SessionPoolKey encoderKey, SessionPoolKey decoderKey) = await BuildDualPooledKeysAsync(
            engineFamily, encoderModelPath, decoderModelPath, selections,
            bootstrap.DevicePolicy, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            modelId, variant, cancellationToken).ConfigureAwait(false);

        return await AcquireDualPooledSessionsAsync(
            provider, bootstrap, selections,
            encoderModelPath, decoderModelPath,
            encoderKey, decoderKey, pool, cancellationToken, sessionFactory)
            .ConfigureAwait(false);
    }

    public static async Task<Qwen3AsrSessionLease> CreatePooledQwen3AsrAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderInitModelPath,
        string decoderStepModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        // Per-role TRT eligibility: a contrib-op decoder must not force the (often much larger)
        // encoder off TensorRT RTX. Encoder is the cold-load hot path (qwen3-asr encoder ~711MB).
        ExecutionProviderKind encoderProvider = DowngradeTrtRtxForUnsupportedGraphs(
            provider,
            [encoderModelPath],
            out string? encoderGraphReason);
        ExecutionProviderKind decoderProvider = DowngradeTrtRtxForUnsupportedGraphs(
            provider,
            [decoderInitModelPath, decoderStepModelPath],
            out string? decoderGraphReason);
        string? graphFallbackReason = MergeFallbackReasons(encoderGraphReason, decoderGraphReason);

        string requestedProvider = FormatProviderLabel(provider);
        ExecutionProviderKind bootstrapProvider =
            encoderProvider is ExecutionProviderKind.TensorRTRtx ||
            decoderProvider is ExecutionProviderKind.TensorRTRtx
                ? ExecutionProviderKind.TensorRTRtx
                : encoderProvider;
        var bootstrapResult = await BootstrapTrackedAsync(bootstrapProvider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(new SessionOptionsRequest(
            encoderProvider == bootstrapProvider
                ? ResolveSessionOptionsProvider(encoderProvider, bootstrapResult.SelectedProvider)
                : encoderProvider,
            devicePolicy,
            additionalTrtEncoderOptions,
            TrtModelPath: encoderModelPath));
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(new SessionOptionsRequest(
            decoderProvider == bootstrapProvider
                ? ResolveSessionOptionsProvider(decoderProvider, bootstrapResult.SelectedProvider)
                : decoderProvider,
            devicePolicy,
            additionalTrtDecoderOptions,
            TrtModelPath: decoderInitModelPath));

        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderInitOptions = decoderOptionsSelection.Options;
        SessionOptions decoderStepOptions = decoderOptionsSelection.Options;

        ExecutionProviderKind encoderOptionsSelectedProvider = encoderOptionsSelection.SelectedProvider;
        ExecutionProviderKind decoderOptionsSelectedProvider = decoderOptionsSelection.SelectedProvider;

        string encoderOptionsFingerprint = BuildSessionOptionsFingerprint(encoderOptionsSelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        string decoderOptionsFingerprint = BuildSessionOptionsFingerprint(decoderOptionsSelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        SessionPoolKey[] qwenKeys = await SessionPoolKey.CreateManyAsync(
            [
                new SessionPoolKey.PooledKeyRequest(
                    engineFamily, modelId, variant, encoderOptionsSelection.SelectedProvider,
                    encoderModelPath, null, "encoder", encoderOptionsFingerprint),
                new SessionPoolKey.PooledKeyRequest(
                    engineFamily, modelId, variant, decoderOptionsSelection.SelectedProvider,
                    decoderInitModelPath, null, "decoder-init", decoderOptionsFingerprint),
                new SessionPoolKey.PooledKeyRequest(
                    engineFamily, modelId, variant, decoderOptionsSelection.SelectedProvider,
                    decoderStepModelPath, null, "decoder-step", decoderOptionsFingerprint),
            ],
            cancellationToken).ConfigureAwait(false);
        SessionPoolKey encoderKey = WithOpenVinoResidency(qwenKeys[0]);
        SessionPoolKey decoderInitKey = WithOpenVinoResidency(qwenKeys[1]);
        SessionPoolKey decoderStepKey = WithOpenVinoResidency(qwenKeys[2]);

        // Atomic bundle (audit §3A): never hold the encoder while waiting on decoders.
        SessionLeaseBundle bundle = await pool.GetLeaseBundleAsync(
            [
                new SessionLeaseRequest(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, encoderOptionsSelection.Options, sessionFactory, ct, encoderOptionsSelection.SelectedProvider, devicePolicy))),
                new SessionLeaseRequest(
                    decoderInitKey,
                    ct => Task.FromResult(CreateSession(decoderInitModelPath, decoderInitOptions, sessionFactory, ct, decoderOptionsSelectedProvider, devicePolicy))),
                new SessionLeaseRequest(
                    decoderStepKey,
                    ct => Task.FromResult(CreateSession(decoderStepModelPath, decoderStepOptions, sessionFactory, ct, decoderOptionsSelectedProvider, devicePolicy))),
            ],
            cancellationToken).ConfigureAwait(false);

        try
        {
            SessionLease encoderPoolLease = bundle.LeaseAt(0);
            SessionLease decoderInitPoolLease = bundle.LeaseAt(1);
            SessionLease decoderStepPoolLease = bundle.LeaseAt(2);

            ExecutionProviderKind encoderEffective = ResolveEffectiveProviderKindFromSession(
                encoderPoolLease.Session,
                encoderOptionsSelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, encoderOptionsSelectedProvider));
            ExecutionProviderKind decoderInitEffective = ResolveEffectiveProviderKindFromSession(
                decoderInitPoolLease.Session,
                decoderOptionsSelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider));
            ExecutionProviderKind decoderStepEffective = ResolveEffectiveProviderKindFromSession(
                decoderStepPoolLease.Session,
                decoderOptionsSelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, decoderOptionsSelectedProvider));
            ExecutionProviderKind effectiveProvider =
                encoderEffective == decoderInitEffective && encoderEffective == decoderStepEffective
                    ? encoderEffective
                    : ExecutionProviderKind.Cpu;
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? encoderFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                encoderEffective,
                encoderOptionsSelection);
            string? decoderInitFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                decoderInitEffective,
                decoderOptionsSelection);
            string? decoderStepFallbackReason = BuildSessionOptionsFallbackReason(
                provider,
                decoderStepEffective,
                decoderOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(
                    graphFallbackReason,
                    MergeFallbackReasons(
                        encoderFallbackReason,
                        MergeFallbackReasons(decoderInitFallbackReason, decoderStepFallbackReason))));

            CpuExecutionAdmission.Shared.RegisterSession(encoderPoolLease.Session, encoderEffective);
            CpuExecutionAdmission.Shared.RegisterSession(decoderInitPoolLease.Session, decoderInitEffective);
            CpuExecutionAdmission.Shared.RegisterSession(decoderStepPoolLease.Session, decoderStepEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(encoderPoolLease.Session, encoderEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(decoderInitPoolLease.Session, decoderInitEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(decoderStepPoolLease.Session, decoderStepEffective);
            return new Qwen3AsrSessionLease(
                encoderPoolLease.Session,
                decoderInitPoolLease.Session,
                decoderStepPoolLease.Session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail)
            {
                PoolBundle = bundle,
                EncoderPoolLease = encoderPoolLease,
                DecoderInitPoolLease = decoderInitPoolLease,
                DecoderStepPoolLease = decoderStepPoolLease,
            };
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public static async Task<LatentSyncSessionLease> CreatePooledLatentSyncAsync(
        string engineFamily,
        string unetModelPath,
        string vaeEncoderModelPath,
        string vaeDecoderModelPath,
        string whisperEncoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await BootstrapTrackedAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        ExecutionProviderKind sessionProvider = ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider);
        SessionOptionsSelection unetOptionsSelection = CreateSessionOptions(new SessionOptionsRequest(sessionProvider, devicePolicy, TrtModelPath: unetModelPath));
        SessionOptionsSelection vaeEncOptionsSelection = CreateSessionOptions(new SessionOptionsRequest(sessionProvider, devicePolicy, TrtModelPath: vaeEncoderModelPath));
        SessionOptionsSelection vaeDecOptionsSelection = CreateSessionOptions(new SessionOptionsRequest(sessionProvider, devicePolicy, TrtModelPath: vaeDecoderModelPath));
        SessionOptionsSelection whisperOptionsSelection = CreateSessionOptions(new SessionOptionsRequest(sessionProvider, devicePolicy, TrtModelPath: whisperEncoderModelPath));
        using SessionOptions unetOptions = unetOptionsSelection.Options;
        using SessionOptions vaeEncOptions = vaeEncOptionsSelection.Options;
        using SessionOptions vaeDecOptions = vaeDecOptionsSelection.Options;
        using SessionOptions whisperOptions = whisperOptionsSelection.Options;

        ExecutionProviderKind unetSelectedProvider = unetOptionsSelection.SelectedProvider;
        ExecutionProviderKind whisperSelectedProvider = whisperOptionsSelection.SelectedProvider;

        string unetOptionsFingerprint = BuildSessionOptionsFingerprint(
            unetOptionsSelection.SelectedProvider, devicePolicy, additionalTrtOptions: null);
        string vaeEncOptionsFingerprint = BuildSessionOptionsFingerprint(
            vaeEncOptionsSelection.SelectedProvider, devicePolicy, additionalTrtOptions: null);
        string vaeDecOptionsFingerprint = BuildSessionOptionsFingerprint(
            vaeDecOptionsSelection.SelectedProvider, devicePolicy, additionalTrtOptions: null);
        string whisperOptionsFingerprint = BuildSessionOptionsFingerprint(
            whisperOptionsSelection.SelectedProvider, devicePolicy, additionalTrtOptions: null);

        const string poolEngineFamily = "latentsync-diffusion";
        SessionPoolKey[] latentKeys = await SessionPoolKey.CreateManyAsync(
            [
                new SessionPoolKey.PooledKeyRequest(
                    poolEngineFamily, modelId, variant, unetOptionsSelection.SelectedProvider,
                    unetModelPath, null, "unet", unetOptionsFingerprint),
                new SessionPoolKey.PooledKeyRequest(
                    poolEngineFamily, modelId, variant, vaeEncOptionsSelection.SelectedProvider,
                    vaeEncoderModelPath, null, "vae-encoder", vaeEncOptionsFingerprint),
                new SessionPoolKey.PooledKeyRequest(
                    poolEngineFamily, modelId, variant, vaeDecOptionsSelection.SelectedProvider,
                    vaeDecoderModelPath, null, "vae-decoder", vaeDecOptionsFingerprint),
                new SessionPoolKey.PooledKeyRequest(
                    poolEngineFamily, modelId, variant, whisperOptionsSelection.SelectedProvider,
                    whisperEncoderModelPath, null, "whisper-encoder", whisperOptionsFingerprint),
            ],
            cancellationToken).ConfigureAwait(false);
        SessionPoolKey unetKey = WithOpenVinoResidency(latentKeys[0]);
        SessionPoolKey vaeEncKey = WithOpenVinoResidency(latentKeys[1]);
        SessionPoolKey vaeDecKey = WithOpenVinoResidency(latentKeys[2]);
        SessionPoolKey whisperKey = WithOpenVinoResidency(latentKeys[3]);

        // Atomic bundle (audit §3A): acquire all four graphs or none.
        SessionLeaseBundle bundle = await pool.GetLeaseBundleAsync(
            [
                new SessionLeaseRequest(
                    unetKey,
                    ct => Task.FromResult(CreateSession(unetModelPath, unetOptions, sessionFactory, ct, unetSelectedProvider, devicePolicy))),
                new SessionLeaseRequest(
                    vaeEncKey,
                    ct => Task.FromResult(CreateSession(vaeEncoderModelPath, vaeEncOptions, sessionFactory, ct, vaeEncOptionsSelection.SelectedProvider, devicePolicy))),
                new SessionLeaseRequest(
                    vaeDecKey,
                    ct => Task.FromResult(CreateSession(vaeDecoderModelPath, vaeDecOptions, sessionFactory, ct, vaeDecOptionsSelection.SelectedProvider, devicePolicy))),
                new SessionLeaseRequest(
                    whisperKey,
                    ct => Task.FromResult(CreateSession(whisperEncoderModelPath, whisperOptions, sessionFactory, ct, whisperSelectedProvider, devicePolicy))),
            ],
            cancellationToken).ConfigureAwait(false);

        try
        {
            SessionLease unetPoolLease = bundle.LeaseAt(0);
            SessionLease vaeEncPoolLease = bundle.LeaseAt(1);
            SessionLease vaeDecPoolLease = bundle.LeaseAt(2);
            SessionLease whisperPoolLease = bundle.LeaseAt(3);

            ExecutionProviderKind unetEffective = ResolveEffectiveProviderKindFromSession(
                unetPoolLease.Session,
                unetOptionsSelection.SelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, unetOptionsSelection.SelectedProvider));
            ExecutionProviderKind vaeEncEffective = ResolveEffectiveProviderKindFromSession(
                vaeEncPoolLease.Session,
                vaeEncOptionsSelection.SelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, vaeEncOptionsSelection.SelectedProvider));
            ExecutionProviderKind vaeDecEffective = ResolveEffectiveProviderKindFromSession(
                vaeDecPoolLease.Session,
                vaeDecOptionsSelection.SelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, vaeDecOptionsSelection.SelectedProvider));
            ExecutionProviderKind whisperEffective = ResolveEffectiveProviderKindFromSession(
                whisperPoolLease.Session,
                whisperOptionsSelection.SelectedProvider,
                ShouldUseCatalogDevicePolicy(devicePolicy, whisperOptionsSelection.SelectedProvider));
            ExecutionProviderKind effective =
                unetEffective == vaeEncEffective
                    && unetEffective == vaeDecEffective
                    && unetEffective == whisperEffective
                    ? unetEffective
                    : ExecutionProviderKind.Cpu;
            string selectedProvider = FormatProviderLabel(effective);
            string? unetFallbackReason = BuildSessionOptionsFallbackReason(
                provider, unetEffective, unetOptionsSelection);
            string? vaeEncFallbackReason = BuildSessionOptionsFallbackReason(
                provider, vaeEncEffective, vaeEncOptionsSelection);
            string? vaeDecFallbackReason = BuildSessionOptionsFallbackReason(
                provider, vaeDecEffective, vaeDecOptionsSelection);
            string? whisperFallbackReason = BuildSessionOptionsFallbackReason(
                provider, whisperEffective, whisperOptionsSelection);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                MergeFallbackReasons(
                    MergeFallbackReasons(unetFallbackReason, vaeEncFallbackReason),
                    MergeFallbackReasons(vaeDecFallbackReason, whisperFallbackReason)));

            CpuExecutionAdmission.Shared.RegisterSession(unetPoolLease.Session, unetEffective);
            CpuExecutionAdmission.Shared.RegisterSession(vaeEncPoolLease.Session, vaeEncEffective);
            CpuExecutionAdmission.Shared.RegisterSession(vaeDecPoolLease.Session, vaeDecEffective);
            CpuExecutionAdmission.Shared.RegisterSession(whisperPoolLease.Session, whisperEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(unetPoolLease.Session, unetEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(vaeEncPoolLease.Session, vaeEncEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(vaeDecPoolLease.Session, vaeDecEffective);
            TensorRtRtxTeardownGuard.ReconcileTracking(whisperPoolLease.Session, whisperEffective);
            return new LatentSyncSessionLease(
                unetPoolLease.Session,
                vaeEncPoolLease.Session,
                vaeDecPoolLease.Session,
                whisperPoolLease.Session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail)
            {
                PoolBundle = bundle,
                UNetPoolLease = unetPoolLease,
                VaeEncoderPoolLease = vaeEncPoolLease,
                VaeDecoderPoolLease = vaeDecPoolLease,
                WhisperEncoderPoolLease = whisperPoolLease,
            };
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public static async Task<NemotronAsrSessionLease> CreatePooledNemotronAsrAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderJointModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        provider = DowngradeTrtRtxForUnsupportedGraphs(
            provider,
            [encoderModelPath, decoderJointModelPath],
            out _);

        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        // CUDA graph replay requires every binding to stay at a fixed address across runs.
        // This flag must stay off here: the dual-session factory is shared with callers whose
        // callsites rebind fresh OrtValues (ParakeetTdtGreedyDecoder, smoke probes), and Nemotron
        // can fall back to managed feeds when shared device allocators are unavailable.
        // Explicit per-caller graph options also flow into the pool-key fingerprint so capture
        // modes cannot collide.
        const bool enableEncoderCudaGraph = false;
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions, enableEncoderCudaGraph);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        (SessionPoolKey encoderKey, SessionPoolKey decoderKey) = await BuildDualPooledKeysAsync(
            engineFamily, encoderModelPath, decoderJointModelPath, selections,
            bootstrap.DevicePolicy, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            modelId, variant, cancellationToken, enableEncoderCudaGraph).ConfigureAwait(false);

        DualPooledLeasePair pair = await AcquireDualPooledSessionsAsync(
            provider, bootstrap, selections,
            encoderModelPath, decoderJointModelPath,
            encoderKey, decoderKey, pool, cancellationToken, sessionFactory)
            .ConfigureAwait(false);

        return new NemotronAsrSessionLease(
            pair.EncoderLease.Session,
            pair.DecoderLease.Session,
            pair.RequestedProviderLabel,
            FormatProviderLabel(pair.SelectedProvider),
            pair.BootstrapDetail)
        {
            EncoderPoolLease = pair.EncoderLease,
            DecoderJointPoolLease = pair.DecoderLease,
        };
    }

    public static async Task<OpusSessionLease> CreatePooledOpusAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        InferenceSessionPool? pool = null,
        string? modelId = null,
        string? variant = null,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null,
        Func<string, SessionOptions, InferenceSession>? sessionFactory = null)
    {
        ArgumentNullException.ThrowIfNull(engineFamily);
        pool ??= InferenceSessionPool.Shared;

        provider = DowngradeTrtRtxForUnsupportedGraphs(
            provider,
            [encoderModelPath, decoderModelPath],
            out _);

        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            encoderModelPath: encoderModelPath, decoderModelPath: decoderModelPath);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        (SessionPoolKey encoderKey, SessionPoolKey decoderKey) = await BuildDualPooledKeysAsync(
            engineFamily, encoderModelPath, decoderModelPath, selections,
            bootstrap.DevicePolicy, additionalTrtEncoderOptions, additionalTrtDecoderOptions,
            modelId, variant, cancellationToken).ConfigureAwait(false);

        DualPooledLeasePair pair = await AcquireDualPooledSessionsAsync(
            provider, bootstrap, selections,
            encoderModelPath, decoderModelPath,
            encoderKey, decoderKey, pool, cancellationToken, sessionFactory)
            .ConfigureAwait(false);

        return new OpusSessionLease(
            pair.EncoderLease.Session, pair.DecoderLease.Session,
            pair.RequestedProviderLabel, FormatProviderLabel(pair.SelectedProvider), pair.BootstrapDetail)
        {
            SelectedProviderKind = pair.SelectedProvider,
            EncoderPoolLease = pair.EncoderLease,
            DecoderPoolLease = pair.DecoderLease
        };
    }

}
