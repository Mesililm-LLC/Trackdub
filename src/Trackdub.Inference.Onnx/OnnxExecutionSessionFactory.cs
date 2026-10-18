using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
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

internal static class OnnxExecutionSessionFactory
{
    private static string ExtractLogId(string modelPath)
    {
        string fileName = Path.GetFileNameWithoutExtension(modelPath);
        string[] parts = modelPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Length >= 3 ? $"{parts[^3]}/{fileName}" : fileName;
    }

    // Canonical EP names returned by ORT device discovery. TensorRT RTX uses the standalone
    // EP ABI plugin, not the Windows ML catalog spelling. Single source of truth — must match the
    // name the plugin is registered under (see TensorRtRtxPluginService.RegistrationName); ORT reports
    // OrtEpDevice.EpName as that registration name.
    private const string TensorRtRtxExecutionProviderName = TensorRtRtxProviderConstants.PluginOrtExecutionProviderName;
    private const string DnnlExecutionProviderName = DnnlOrtProbe.OrtExecutionProviderName;
    private const string DnnlUpperExecutionProviderName = DnnlOrtProbe.OrtExecutionProviderNameUpper;
    private const string DirectMlExecutionProviderName = "DmlExecutionProvider";
    private const string ClassicTensorRtExecutionProviderName = "TensorrtExecutionProvider";
    private const string DirectMlLongExecutionProviderName = "DirectMLExecutionProvider";
    private const string CacheRootEnvironmentVariable = "TRACKDUB_CACHE_ROOT";
    private const string EngineCacheRootEnvironmentVariable = "TRACKDUB_ENGINE_CACHE_ROOT";

    private static readonly object InitializeLock = new();
    private static int initializeCompleted;

    private static IExecutionProviderBootstrapper _bootstrapper = GetPlatformBootstrapper();
    private static IOpenVinoAvailabilityProvider _openVinoAvailability = NullOpenVinoAvailabilityProvider.Instance;
    private static IWindowsMlEpDevicePolicyProvider _devicePolicyProvider = NullWindowsMlEpDevicePolicyProvider.Instance;
    private static ILogger? _logger;
    private static readonly ConcurrentDictionary<string, byte> WarnedUnmappedCatalogEpNames =
        new(StringComparer.OrdinalIgnoreCase);

    internal static bool UseOpenVinoCpuProxy => _openVinoAvailability.UseOpenVinoCpuProxy;

    /// <summary>
    /// One-time process initialization. The first successful call wins; later calls are ignored.
    /// </summary>
    internal static void Initialize(
        IExecutionProviderBootstrapper bootstrapper,
        IWindowsMlEpDevicePolicyProvider? devicePolicyProvider = null,
        ILogger? logger = null,
        IOpenVinoAvailabilityProvider? openVinoAvailabilityProvider = null)
    {
        lock (InitializeLock)
        {
            if (initializeCompleted != 0)
            {
                return;
            }

            _bootstrapper = bootstrapper ?? throw new ArgumentNullException(nameof(bootstrapper));
            _devicePolicyProvider = devicePolicyProvider ?? NullWindowsMlEpDevicePolicyProvider.Instance;
            _openVinoAvailability = openVinoAvailabilityProvider ?? NullOpenVinoAvailabilityProvider.Instance;
            _logger = logger;
            initializeCompleted = 1;
        }
    }

    /// <summary>
    /// Resets process-level static state for test isolation. Not for production use.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (InitializeLock)
        {
            initializeCompleted = 0;
            _bootstrapper = GetPlatformBootstrapper();
            _devicePolicyProvider = NullWindowsMlEpDevicePolicyProvider.Instance;
            _openVinoAvailability = NullOpenVinoAvailabilityProvider.Instance;
            _logger = null;
            WarnedUnmappedCatalogEpNames.Clear();
        }
    }

    private sealed record BootstrapContext(
        ExecutionProviders.ExecutionProviderBootstrapResult Bootstrap,
        WindowsMlExecutionDevicePolicy DevicePolicy,
        string RequestedProviderLabel);

    private sealed record DualOptionsSelections(
        SessionOptionsSelection Encoder,
        SessionOptionsSelection Decoder);

    private sealed record DualSessionMetadata(
        ExecutionProviderKind SelectedProvider,
        ExecutionProviderKind EncoderProvider,
        ExecutionProviderKind DecoderProvider,
        string? BootstrapDetail);

    private sealed record DualPooledLeasePair(
        SessionLeaseBundle Bundle,
        string RequestedProviderLabel,
        ExecutionProviderKind SelectedProvider,
        string? BootstrapDetail)
    {
        /// <summary>Caller-order index 0 (encoder). See <c>AcquireDualPooledSessionsAsync</c>.</summary>
        public SessionLease EncoderLease => Bundle.LeaseAt(0);

        /// <summary>Caller-order index 1 (decoder).</summary>
        public SessionLease DecoderLease => Bundle.LeaseAt(1);
    }

    private static async Task<BootstrapContext> BootstrapForProviderAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        return new BootstrapContext(bootstrapResult, devicePolicy, FormatProviderLabel(provider));
    }

    /// <summary>
    /// Readiness-only registration for smoke probes: never downloads, and reports the provider
    /// the bootstrapper can actually select. Callers must treat <see cref="ExecutionProviders.ExecutionProviderBootstrapResult.IsRequestFulfilled"/>
    /// of <see langword="false"/> as an unproven pair — registration alone is not readiness.
    /// </summary>
    internal static async Task<ExecutionProviders.ExecutionProviderBootstrapResult> BootstrapForSmokeAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken)
    {
        // Native ONNX Runtime / DirectML provider DLLs must be resolvable before the first
        // OrtEnv touch; smoke runs in headless hosts that otherwise hit OrtEnv via discovery
        // first and can no longer append DirectML.
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        return await _bootstrapper.BootstrapAsync(provider, allowDownloads: false, cancellationToken)
            .ConfigureAwait(false);
    }

    private static DualOptionsSelections CreateDualOptionsSelections(
        ExecutionProviderKind provider,
        ExecutionProviders.ExecutionProviderBootstrapResult bootstrapResult,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions,
        bool enableEncoderCudaGraph = false, string? encoderLogId = null, string? decoderLogId = null)
    {
        ExecutionProviderKind sessionProvider =
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider);
        return new DualOptionsSelections(
            CreateSessionOptions(sessionProvider, devicePolicy, additionalTrtEncoderOptions, enableEncoderCudaGraph, encoderLogId),
            CreateSessionOptions(sessionProvider, devicePolicy, additionalTrtDecoderOptions, enableCudaGraph: false, decoderLogId));
    }

    private static DualSessionMetadata ResolveDualSessionMetadata(
        ExecutionProviderKind requestedProvider,
        BootstrapContext bootstrap,
        DualOptionsSelections selections,
        InferenceSession encoderSession,
        InferenceSession decoderSession)
    {
        ExecutionProviderKind encoderEffective = ResolveEffectiveProviderKindFromSession(
            encoderSession,
            selections.Encoder.SelectedProvider,
            ShouldUseCatalogDevicePolicy(bootstrap.DevicePolicy, selections.Encoder.SelectedProvider));
        ExecutionProviderKind decoderEffective = ResolveEffectiveProviderKindFromSession(
            decoderSession,
            selections.Decoder.SelectedProvider,
            ShouldUseCatalogDevicePolicy(bootstrap.DevicePolicy, selections.Decoder.SelectedProvider));
        ExecutionProviderKind resolvedSelectedProvider = ResolveSharedSelectedProvider(
            requestedProvider, encoderEffective, decoderEffective);
        string? encoderFallbackReason = BuildSessionOptionsFallbackReason(
            requestedProvider, encoderEffective, selections.Encoder);
        string? decoderFallbackReason = BuildSessionOptionsFallbackReason(
            requestedProvider, decoderEffective, selections.Decoder);
        return new DualSessionMetadata(
            resolvedSelectedProvider,
            encoderEffective,
            decoderEffective,
            FormatBootstrapDetail(
                bootstrap.Bootstrap.Detail,
                MergeFallbackReasons(encoderFallbackReason, decoderFallbackReason)));
    }

    private static async Task<(SessionPoolKey EncoderKey, SessionPoolKey DecoderKey)> BuildDualPooledKeysAsync(
        string engineFamily,
        string encoderModelPath,
        string decoderModelPath,
        DualOptionsSelections selections,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions,
        string? modelId,
        string? variant,
        CancellationToken cancellationToken,
        bool enableEncoderCudaGraph = false, string? encoderLogId = null, string? decoderLogId = null)
    {
        string encoderFingerprint = BuildSessionOptionsFingerprint(
            selections.Encoder.SelectedProvider, devicePolicy, additionalTrtEncoderOptions, enableEncoderCudaGraph);
        string decoderFingerprint = BuildSessionOptionsFingerprint(
            selections.Decoder.SelectedProvider, devicePolicy, additionalTrtDecoderOptions);
        SessionPoolKey encoderKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            engineFamily, modelId, variant, selections.Encoder.SelectedProvider,
            encoderModelPath, deviceId: null, graphRole: "encoder", encoderFingerprint, cancellationToken)
            .ConfigureAwait(false));
        SessionPoolKey decoderKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            engineFamily, modelId, variant, selections.Decoder.SelectedProvider,
            decoderModelPath, deviceId: null, graphRole: "decoder", decoderFingerprint, cancellationToken)
            .ConfigureAwait(false));
        return (encoderKey, decoderKey);
    }

    private static async Task<DualPooledLeasePair> AcquireDualPooledSessionsAsync(
        ExecutionProviderKind requestedProvider,
        BootstrapContext bootstrap,
        DualOptionsSelections selections,
        string encoderModelPath,
        string decoderModelPath,
        SessionPoolKey encoderKey,
        SessionPoolKey decoderKey,
        InferenceSessionPool pool,
        CancellationToken cancellationToken,
        Func<string, SessionOptions, InferenceSession>? sessionFactory)
    {
        // Atomic bundle (audit §3A): never hold the encoder lease while blocked on the decoder.
        // Lease order in the bundle is caller order (encoder, decoder). Each graph reports its
        // placement at creation: if the decoder then fails, a CPU-placed encoder must not stay
        // tracked as TensorRT-RTX, or a poisoned runtime would skip its disposal and leak it.
        SessionLeaseBundle bundle = await pool.GetLeaseBundleAsync(
            [
                new SessionLeaseRequest(
                    encoderKey,
                    ct => Task.FromResult(CreateSession(encoderModelPath, selections.Encoder.Options, sessionFactory, ct, selections.Encoder.SelectedProvider, bootstrap.DevicePolicy)),
                    session => ResolveEffectiveProviderKindFromSession(
                        session,
                        selections.Encoder.SelectedProvider,
                        ShouldUseCatalogDevicePolicy(bootstrap.DevicePolicy, selections.Encoder.SelectedProvider))),
                new SessionLeaseRequest(
                    decoderKey,
                    ct => Task.FromResult(CreateSession(decoderModelPath, selections.Decoder.Options, sessionFactory, ct, selections.Decoder.SelectedProvider, bootstrap.DevicePolicy)),
                    session => ResolveEffectiveProviderKindFromSession(
                        session,
                        selections.Decoder.SelectedProvider,
                        ShouldUseCatalogDevicePolicy(bootstrap.DevicePolicy, selections.Decoder.SelectedProvider))),
            ],
            cancellationToken).ConfigureAwait(false);

        try
        {
            DualSessionMetadata metadata = ResolveDualSessionMetadata(
                requestedProvider, bootstrap, selections,
                bundle.LeaseAt(0).Session, bundle.LeaseAt(1).Session);
            CpuExecutionAdmission.Shared.RegisterSession(bundle.LeaseAt(0).Session, metadata.EncoderProvider);
            CpuExecutionAdmission.Shared.RegisterSession(bundle.LeaseAt(1).Session, metadata.DecoderProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(bundle.LeaseAt(0).Session, metadata.EncoderProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(bundle.LeaseAt(1).Session, metadata.DecoderProvider);
            return new DualPooledLeasePair(
                bundle,
                bootstrap.RequestedProviderLabel,
                metadata.SelectedProvider,
                metadata.BootstrapDetail);
        }
        catch
        {
            bundle.Dispose();
            throw;
        }
    }

    public static async Task<SingleSessionLease> CreateSingleAsync(
        string modelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool allowTrtInitFallback = true)
    {
        string requestedProvider = FormatProviderLabel(provider);
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        string logId = ExtractLogId(modelPath);
        SessionOptionsSelection sessionOptionsSelection = CreateSessionOptions(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtOptions,
            logId: logId);
        InferenceSession? session = null;
        try
        {
            (session, sessionOptionsSelection) = CreateInferenceSessionWithTrtInitFallback(
                modelPath,
                sessionOptionsSelection,
                devicePolicy,
                additionalTrtOptions,
                sessionFactory: null,
                cancellationToken,
                allowTrtInitFallback,
                logId: logId);
            bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, sessionOptionsSelection.SelectedProvider);
            ExecutionProviderKind effectiveProvider = ResolveEffectiveProviderKindFromSession(
                session,
                sessionOptionsSelection.SelectedProvider,
                useCatalogDevicePolicy);
            string selectedProvider = FormatProviderLabel(effectiveProvider);
            string? bootstrapDetail = FormatBootstrapDetail(
                bootstrapResult.Detail,
                BuildSessionOptionsFallbackReason(provider, effectiveProvider, sessionOptionsSelection));
            CpuExecutionAdmission.Shared.RegisterSession(session, effectiveProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(session, effectiveProvider);
            return new SingleSessionLease(
                session,
                requestedProvider,
                selectedProvider,
                bootstrapDetail);
        }
        catch (Exception ex)
        {
            // CreateSession tracks TensorRT-RTX sessions before this catch runs. Mark a CUDA
            // OOM first, or DisposeSafely still calls native Dispose on that tracked session.
            ReleaseSessionsAfterCreateFailure(ex, session, decoder: null);
            throw;
        }
        finally
        {
            sessionOptionsSelection.Options.Dispose();
        }
    }

    private static InferenceSession CreateSession(
        string modelPath,
        SessionOptions options,
        Func<string, SessionOptions, InferenceSession>? sessionFactory,
        CancellationToken cancellationToken,
        ExecutionProviderKind selectedProvider,
        WindowsMlExecutionDevicePolicy devicePolicy)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var phase = BenchmarkPhaseCapture.Start("onnx-session-create");
        // EP-context artifacts embed a TensorRT-RTX-serialized engine that only that EP can
        // deserialize; only TRT-RTX sessions may load one (see EpContextWarmupService).
        IReadOnlyDictionary<string, string>? trtOptions = selectedProvider is ExecutionProviderKind.TensorRTRtx
            ? TryGetAppendedTensorRtRtxOptions(options)
            : null;
        string loadPath = trtOptions is not null
            ? EpContext.EpContextLoadPathResolver.TryResolveLoadPath(modelPath, trtOptions) ?? modelPath
            : modelPath;
        // Without a current artifact, a large graph's engine build is captured as one, so the next
        // process loads engines instead of rebuilding them (minutes for multi-GB models).
        InferenceSession session;
        try
        {
            session =
                sessionFactory is null &&
                trtOptions is not null &&
                string.Equals(loadPath, modelPath, StringComparison.Ordinal) &&
                EpContext.EpContextCapture.ShouldCapture(modelPath)
                    ? EpContext.EpContextCapture.CreateSession(modelPath, options, trtOptions, static (path, o) => new InferenceSession(path, o))
                    : sessionFactory is null
                        ? new InferenceSession(loadPath, options)
                        : sessionFactory(loadPath, options);
        }
        catch (Exception ex) when (selectedProvider is ExecutionProviderKind.TensorRTRtx)
        {
            TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(ex);
            throw;
        }

        if (selectedProvider is ExecutionProviderKind.TensorRTRtx)
        {
            TensorRtRtxTeardownGuard.Track(session);
        }

#if WINDOWS
        // MS "Efficiency tips for Windows ML inference": express efficiency intent through ORT
        // (ep.dynamic.workload_type) for NPU catalog sessions under efficiency-flavored device
        // policies. Only NPU EPs act on the signal; the call is a no-op elsewhere but is gated
        // to NPU providers to keep the no-op surface minimal. Outcomes are logged, never assumed.
        // This hook runs strictly post-registration: an NPU catalog session can only exist here
        // if WindowsMlProviderRegistrationPolicy already readied + TryRegister()-ed the EP
        // (MS "Register Windows ML execution providers") — no new install/registration logic
        // is introduced, and the hook never fires when the EP was not registered.
        (bool efficiencyApplied, string? efficiencyError) =
            WindowsMl.WindowsMlEfficiencyPolicy.ApplyToSession(session, selectedProvider, devicePolicy);
        if (efficiencyApplied || efficiencyError is not null)
        {
            string efficiencySessionLabel =
                string.IsNullOrEmpty(options.LogId) ? Path.GetFileName(modelPath) : options.LogId;
            if (efficiencyApplied)
            {
                _logger?.LogInformation(
                    $"Session '{efficiencySessionLabel}' ({FormatProviderLabel(selectedProvider)}): " +
                    "ep.dynamic.workload_type=Efficient applied.");
            }
            else
            {
                _logger?.LogWarning(
                    $"Session '{efficiencySessionLabel}' ({FormatProviderLabel(selectedProvider)}): " +
                    $"ep.dynamic.workload_type=Efficient was NOT applied: {efficiencyError}");
            }
        }
#endif

        return session;
    }

    /// <summary>
    /// Creates an ORT session; when TensorRT RTX was selected and init fails due to unsupported
    /// ops/kernels, optionally retries once with DirectML (Windows) then CPU and records FallbackReason.
    /// Pass <paramref name="allowTrtInitFallback"/> <see langword="false"/> for hard-pin routes
    /// (<c>RequirePreferredExecutionProvider</c> / CLI <c>--require-execution-provider</c>).
    /// </summary>
    internal static (InferenceSession Session, SessionOptionsSelection Selection) CreateInferenceSessionWithTrtInitFallback(
        string modelPath,
        SessionOptionsSelection initialSelection,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        Func<string, SessionOptions, InferenceSession>? sessionFactory,
        CancellationToken cancellationToken,
        bool allowTrtInitFallback = true,
        string? logId = null)
    {
        // Honest skip: graphs with com.microsoft contrib fusions cannot be imported by
        // TensorRT-RTX (all-or-nothing ONNX catalog). Do not attempt TRT and do not emit
        // the ModelImporter error storm — fall through to DirectML/CPU immediately.
        if (initialSelection.SelectedProvider is ExecutionProviderKind.TensorRTRtx
            && TrtRtxUnsupportedOpScanner.FindUnsupportedOps(modelPath) is { Count: > 0 } unsupportedOps)
        {
            string opList = string.Join(", ", unsupportedOps);
            string skipReason =
                $"TensorRT-RTX cannot import '{Path.GetFileName(modelPath)}' (unsupported ops: {opList}).";

            if (!allowTrtInitFallback)
            {
                initialSelection.Options.Dispose();
                throw new InvalidOperationException($"{skipReason} Hard-pin route has no fallback.");
            }

            return CreateUnsupportedGraphFallback(modelPath, initialSelection, devicePolicy,
                sessionFactory, cancellationToken, skipReason, logId: logId);
        }

        try
        {
            InferenceSession session = CreateSession(
                modelPath,
                initialSelection.Options,
                sessionFactory,
                cancellationToken,
                initialSelection.SelectedProvider,
                devicePolicy);
            return (session, initialSelection);
        }
        catch (Exception ex) when (
            allowTrtInitFallback
            && initialSelection.SelectedProvider is ExecutionProviderKind.TensorRTRtx
            && LooksLikeTrtSessionInitFailure(ex))
        {
            return CreateTrtInitFailureFallback(modelPath, initialSelection, devicePolicy,
                sessionFactory, cancellationToken, ex, logId: logId);
        }
    }

    private static (InferenceSession Session, SessionOptionsSelection Selection) CreateUnsupportedGraphFallback(
        string modelPath, SessionOptionsSelection initialSelection, WindowsMlExecutionDevicePolicy devicePolicy,
        Func<string, SessionOptions, InferenceSession>? sessionFactory, CancellationToken cancellationToken,
        string skipReason, string? logId = null)
    {
        Exception? lastFailure = null;
        foreach (SessionOptionsSelection fallbackSelection in EnumerateTrtInitFallbackProviders()
                     .Select(provider => CreateSessionOptions(provider, devicePolicy, additionalTrtOptions: null, logId: logId)))
        {
            try
            {
                InferenceSession session = CreateSession(modelPath, fallbackSelection.Options, sessionFactory,
                    cancellationToken, fallbackSelection.SelectedProvider, devicePolicy);
                initialSelection.Options.Dispose();
                string label = FormatProviderLabel(fallbackSelection.SelectedProvider);
                return (session, new SessionOptionsSelection(fallbackSelection.Options,
                    fallbackSelection.SelectedProvider,
                    MergeFallbackReasons($"{skipReason} Selected {label}.", fallbackSelection.FallbackReason)));
            }
            catch (Exception fallbackEx)
            {
                fallbackSelection.Options.Dispose();
                if (!IsRecoverableTrtFallbackInitFailure(fallbackEx)) throw;
                lastFailure = fallbackEx;
            }
        }

        initialSelection.Options.Dispose();
        throw lastFailure ?? new InvalidOperationException(skipReason);
    }

    private static (InferenceSession Session, SessionOptionsSelection Selection) CreateTrtInitFailureFallback(
        string modelPath, SessionOptionsSelection initialSelection, WindowsMlExecutionDevicePolicy devicePolicy,
        Func<string, SessionOptions, InferenceSession>? sessionFactory, CancellationToken cancellationToken,
        Exception originalFailure, string? logId = null)
    {
        string trtError = SummarizeExceptionMessage(originalFailure);
        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(originalFailure);
        Exception lastFailure = originalFailure;
        foreach (SessionOptionsSelection fallbackSelection in EnumerateTrtInitFallbackProviders()
                     .Select(provider => CreateSessionOptions(provider, devicePolicy, additionalTrtOptions: null, logId: logId)))
        {
            try
            {
                InferenceSession session = CreateSession(modelPath, fallbackSelection.Options, sessionFactory,
                    cancellationToken, fallbackSelection.SelectedProvider, devicePolicy);
                // Transfer ownership: the caller owns fallback options after success.
                initialSelection.Options.Dispose();
                string label = FormatProviderLabel(fallbackSelection.SelectedProvider);
                string reason = $"TensorRT RTX session init failed ({trtError}); fell back to {label}.";
                return (session, new SessionOptionsSelection(fallbackSelection.Options,
                    fallbackSelection.SelectedProvider, MergeFallbackReasons(reason, fallbackSelection.FallbackReason)));
            }
            catch (Exception fallbackEx)
            {
                fallbackSelection.Options.Dispose();
                if (!IsRecoverableTrtFallbackInitFailure(fallbackEx)) throw;
                lastFailure = fallbackEx;
            }
        }

        // Leave initialSelection.Options for the caller to dispose.
        throw lastFailure;
    }

    /// <summary>
    /// Rewrites a TensorRT-RTX request to DirectML/CPU when any model graph contains
    /// contrib ops the TRT-RTX ONNX parser cannot import. Multi-session factories call this
    /// before building session options so they never open a doomed TRT session.
    /// </summary>
    private static ExecutionProviderKind DowngradeTrtRtxForUnsupportedGraphs(
        ExecutionProviderKind provider,
        IReadOnlyList<string> modelPaths,
        out string? fallbackReason)
    {
        fallbackReason = null;
        if (provider is not ExecutionProviderKind.TensorRTRtx)
        {
            return provider;
        }

        foreach (string path in modelPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            IReadOnlyList<string> unsupported = TensorRtRtx.TrtRtxUnsupportedOpScanner.FindUnsupportedOps(path);
            if (unsupported.Count == 0)
            {
                continue;
            }

            ExecutionProviderKind next = OperatingSystem.IsWindows()
                ? ExecutionProviderKind.DirectMl
                : ExecutionProviderKind.Cpu;
            fallbackReason =
                $"TensorRT-RTX cannot import '{Path.GetFileName(path)}' (unsupported ops: {string.Join(", ", unsupported)}); " +
                $"selected {FormatProviderLabel(next)} without attempting TensorRT-RTX.";
            return next;
        }

        return provider;
    }

    private static IEnumerable<ExecutionProviderKind> EnumerateTrtInitFallbackProviders()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ExecutionProviderKind.DirectMl;
        }

        yield return ExecutionProviderKind.Cpu;
    }

    internal static bool LooksLikeTrtSessionInitFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message;
            if (message.Contains("Kernel not found", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ModelImporter", StringComparison.OrdinalIgnoreCase)
                || message.Contains("No graph will run on TensorRT", StringComparison.OrdinalIgnoreCase)
                || message.Contains("NvTensorRTRTX", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TensorRT-RTX", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TensorRT RTX", StringComparison.OrdinalIgnoreCase)
                || message.Contains("transformer_memcpy", StringComparison.OrdinalIgnoreCase)
                || message.Contains("ProcessInitializers", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRecoverableTrtFallbackInitFailure(Exception exception) =>
        LooksLikeTrtSessionInitFailure(exception)
        || exception is OnnxRuntimeException
        || exception is InvalidOperationException
        || exception is DllNotFoundException
        || exception is EntryPointNotFoundException;

    private static string SummarizeExceptionMessage(Exception exception)
    {
        string message = exception.GetBaseException().Message.ReplaceLineEndings(" ");
        const int maxLength = 240;
        return message.Length <= maxLength ? message : message[..maxLength] + "...";
    }

    public static async Task<WhisperSessionLease> CreateWhisperAsync(
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null)
    {
        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        InferenceSession? encoderSession = null;
        InferenceSession? decoderSession = null;
        try
        {
            // Through CreateSession so smoke probes load (or capture) EP-context artifacts like
            // pooled sessions do, instead of rebuilding every engine from source.
            encoderSession = CreateSession(encoderModelPath, encoderOptions, null, cancellationToken, selections.Encoder.SelectedProvider, bootstrap.DevicePolicy);
            decoderSession = CreateSession(decoderModelPath, decoderOptions, null, cancellationToken, selections.Decoder.SelectedProvider, bootstrap.DevicePolicy);
            DualSessionMetadata metadata = ResolveDualSessionMetadata(
                provider, bootstrap, selections, encoderSession, decoderSession);
            CpuExecutionAdmission.Shared.RegisterSession(encoderSession, metadata.EncoderProvider);
            CpuExecutionAdmission.Shared.RegisterSession(decoderSession, metadata.DecoderProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(encoderSession, metadata.EncoderProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(decoderSession, metadata.DecoderProvider);

            return new WhisperSessionLease(
                encoderSession,
                decoderSession,
                bootstrap.RequestedProviderLabel,
                FormatProviderLabel(metadata.SelectedProvider),
                metadata.BootstrapDetail);
        }
        catch (Exception ex)
        {
            ReleaseSessionsAfterCreateFailure(ex, encoderSession, decoderSession);
            throw;
        }
    }

    public static async Task<OpusSessionLease> CreateOpusAsync(
        string encoderModelPath,
        string decoderModelPath,
        ExecutionProviderKind provider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtEncoderOptions = null,
        IReadOnlyDictionary<string, string>? additionalTrtDecoderOptions = null)
    {
        BootstrapContext bootstrap = await BootstrapForProviderAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        DualOptionsSelections selections = CreateDualOptionsSelections(
            provider, bootstrap.Bootstrap, bootstrap.DevicePolicy,
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
        using SessionOptions encoderOptions = selections.Encoder.Options;
        using SessionOptions decoderOptions = selections.Decoder.Options;
        InferenceSession? encoderSession = null;
        InferenceSession? decoderSession = null;
        try
        {
            // Through CreateSession so smoke probes load (or capture) EP-context artifacts like
            // pooled sessions do, instead of rebuilding every engine from source.
            encoderSession = CreateSession(encoderModelPath, encoderOptions, null, cancellationToken, selections.Encoder.SelectedProvider, bootstrap.DevicePolicy);
            decoderSession = CreateSession(decoderModelPath, decoderOptions, null, cancellationToken, selections.Decoder.SelectedProvider, bootstrap.DevicePolicy);
            DualSessionMetadata metadata = ResolveDualSessionMetadata(
                provider, bootstrap, selections, encoderSession, decoderSession);
            CpuExecutionAdmission.Shared.RegisterSession(encoderSession, metadata.EncoderProvider);
            CpuExecutionAdmission.Shared.RegisterSession(decoderSession, metadata.DecoderProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(encoderSession, metadata.EncoderProvider);
            TensorRtRtxTeardownGuard.ReconcileTracking(decoderSession, metadata.DecoderProvider);

            return new OpusSessionLease(
                encoderSession,
                decoderSession,
                bootstrap.RequestedProviderLabel,
                FormatProviderLabel(metadata.SelectedProvider),
                metadata.BootstrapDetail)
            {
                SelectedProviderKind = metadata.SelectedProvider
            };
        }
        catch (Exception ex)
        {
            ReleaseSessionsAfterCreateFailure(ex, encoderSession, decoderSession);
            throw;
        }
    }

    /// <summary>
    /// Marks a CUDA out-of-memory before disposing sessions from a failed create.
    /// <see cref="CreateSession"/> tracks a TensorRT-RTX session as soon as it exists, so a
    /// decoder failure after a tracked encoder must poison the guard first.
    /// </summary>
    internal static void ReleaseSessionsAfterCreateFailure(
        Exception failure,
        InferenceSession? encoder,
        InferenceSession? decoder)
    {
        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(failure);
        TensorRtRtxTeardownGuard.DisposeSafely(encoder);
        TensorRtRtxTeardownGuard.DisposeSafely(decoder);
    }

    // ── Pooled factory methods ─────────────────────────────────────────────
    //
    // These methods create ONNX sessions and register them with the shared
    // InferenceSessionPool so that sessions are reused across engine calls.
    // Metadata (selected provider, bootstrap detail) is captured at creation
    // time and cached alongside the pool entry for subsequent pool hits.

    private sealed class SessionOptionsDisposeHolder : IDisposable
    {
        public SessionOptionsDisposeHolder(SessionOptions current)
        {
            Current = current;
        }

        public SessionOptions Current { get; set; }

        public void Dispose()
        {
            Current.Dispose();
        }
    }

    /// <summary>
    /// Pre-warms a pooled single session and immediately releases it, leaving the warm
    /// session in <see cref="InferenceSessionPool"/> for a later stage run. Use at startup
    /// or after plan resolution to amortise first-call latency.
    /// </summary>
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
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection optionsSelection = CreateSessionOptions(
            ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider),
            devicePolicy,
            additionalTrtOptions);

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
            decoderLogId: ExtractLogId(decoderModelPath));
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
        var bootstrapResult = await _bootstrapper.BootstrapAsync(bootstrapProvider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        SessionOptionsSelection encoderOptionsSelection = CreateSessionOptions(
            encoderProvider == bootstrapProvider
                ? ResolveSessionOptionsProvider(encoderProvider, bootstrapResult.SelectedProvider)
                : encoderProvider,
            devicePolicy,
            additionalTrtEncoderOptions);
        SessionOptionsSelection decoderOptionsSelection = CreateSessionOptions(
            decoderProvider == bootstrapProvider
                ? ResolveSessionOptionsProvider(decoderProvider, bootstrapResult.SelectedProvider)
                : decoderProvider,
            devicePolicy,
            additionalTrtDecoderOptions);

        using SessionOptions encoderOptions = encoderOptionsSelection.Options;
        using SessionOptions decoderInitOptions = decoderOptionsSelection.Options;
        SessionOptions decoderStepOptions = decoderOptionsSelection.Options;

        ExecutionProviderKind encoderOptionsSelectedProvider = encoderOptionsSelection.SelectedProvider;
        ExecutionProviderKind decoderOptionsSelectedProvider = decoderOptionsSelection.SelectedProvider;

        string encoderOptionsFingerprint = BuildSessionOptionsFingerprint(encoderOptionsSelectedProvider, devicePolicy, additionalTrtEncoderOptions);
        string decoderOptionsFingerprint = BuildSessionOptionsFingerprint(decoderOptionsSelectedProvider, devicePolicy, additionalTrtDecoderOptions);

        SessionPoolKey encoderKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            engineFamily, modelId, variant, encoderOptionsSelection.SelectedProvider,
            encoderModelPath, null, "encoder", encoderOptionsFingerprint, cancellationToken).ConfigureAwait(false));
        SessionPoolKey decoderInitKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            engineFamily, modelId, variant, decoderOptionsSelection.SelectedProvider,
            decoderInitModelPath, null, "decoder-init", decoderOptionsFingerprint, cancellationToken).ConfigureAwait(false));
        SessionPoolKey decoderStepKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            engineFamily, modelId, variant, decoderOptionsSelection.SelectedProvider,
            decoderStepModelPath, null, "decoder-step", decoderOptionsFingerprint, cancellationToken).ConfigureAwait(false));

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
        var bootstrapResult = await _bootstrapper.BootstrapAsync(provider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);

        ExecutionProviderKind sessionProvider = ResolveSessionOptionsProvider(provider, bootstrapResult.SelectedProvider);
        SessionOptionsSelection unetOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        SessionOptionsSelection vaeEncOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        SessionOptionsSelection vaeDecOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
        SessionOptionsSelection whisperOptionsSelection = CreateSessionOptions(sessionProvider, devicePolicy, null);
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
        SessionPoolKey unetKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            poolEngineFamily, modelId, variant, unetOptionsSelection.SelectedProvider,
            unetModelPath, null, "unet", unetOptionsFingerprint, cancellationToken).ConfigureAwait(false));
        SessionPoolKey vaeEncKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            poolEngineFamily, modelId, variant, vaeEncOptionsSelection.SelectedProvider,
            vaeEncoderModelPath, null, "vae-encoder", vaeEncOptionsFingerprint, cancellationToken).ConfigureAwait(false));
        SessionPoolKey vaeDecKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            poolEngineFamily, modelId, variant, vaeDecOptionsSelection.SelectedProvider,
            vaeDecoderModelPath, null, "vae-decoder", vaeDecOptionsFingerprint, cancellationToken).ConfigureAwait(false));
        SessionPoolKey whisperKey = WithOpenVinoResidency(await SessionPoolKey.CreateAsync(
            poolEngineFamily, modelId, variant, whisperOptionsSelection.SelectedProvider,
            whisperEncoderModelPath, null, "whisper-encoder", whisperOptionsFingerprint, cancellationToken).ConfigureAwait(false));

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
            additionalTrtEncoderOptions, additionalTrtDecoderOptions);
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

    internal sealed record SessionOptionsFactoryBundle(
        Func<SessionOptions> CreateOptions,
        ExecutionProviderKind RequestedProvider,
        ExecutionProviderKind SelectedProvider,
        string BootstrapDetail);

    internal static async Task<SessionOptionsFactoryBundle> CreateSessionOptionsFactoryAsync(
        ExecutionProviderKind requestedProvider,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null)
    {
        var bootstrapResult = await _bootstrapper.BootstrapAsync(requestedProvider, allowDownloads: true, cancellationToken)
            .ConfigureAwait(false);
        WindowsMlExecutionDevicePolicy devicePolicy = await ResolveDevicePolicyAsync(cancellationToken)
            .ConfigureAwait(false);
        ExecutionProviderKind sessionProvider = ResolveSessionOptionsProvider(
            requestedProvider,
            bootstrapResult.SelectedProvider);
        SessionOptionsSelection probeSelection = CreateSessionOptions(
            sessionProvider,
            devicePolicy,
            additionalTrtOptions);
        ExecutionProviderKind selectedProvider = probeSelection.SelectedProvider;
        probeSelection.Options.Dispose();

        return new SessionOptionsFactoryBundle(
            () => CreateSessionOptions(sessionProvider, devicePolicy, additionalTrtOptions).Options,
            requestedProvider,
            selectedProvider,
            FormatBootstrapDetail(bootstrapResult.Detail, probeSelection.FallbackReason) ?? string.Empty);
    }

    /// <summary>
    /// Packaged DirectML is included with Windows ML. Catalog RegisterCertifiedAsync is not a
    /// prerequisite for GetEpDevices/append, so a bootstrap CPU select must not skip the DML path.
    /// Native CUDA on Windows still wins when bootstrap selected CUDA.
    /// </summary>
    internal static ExecutionProviderKind ResolveSessionOptionsProvider(
        ExecutionProviderKind requestedProvider,
        ExecutionProviderKind bootstrapSelectedProvider)
    {
        if (requestedProvider is ExecutionProviderKind.DirectMl)
        {
            return OperatingSystem.IsWindows()
                ? ExecutionProviderKind.DirectMl
                : bootstrapSelectedProvider;
        }

        if (requestedProvider is ExecutionProviderKind.Cuda
            && bootstrapSelectedProvider is ExecutionProviderKind.Cpu
            && OperatingSystem.IsWindows())
        {
            return ExecutionProviderKind.DirectMl;
        }

        return bootstrapSelectedProvider;
    }

    internal static string FormatProviderLabel(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.TensorRTRtx => "tensorrt-rtx",
            ExecutionProviderKind.OpenVino => "openvino",
            ExecutionProviderKind.CoreMl => "coreml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRt => "tensorrt",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.Dnnl => "dnnl",
            ExecutionProviderKind.Qnn => "qnn",
            ExecutionProviderKind.OpenVinoCatalog => "openvino-catalog",
            ExecutionProviderKind.VitisAi => "vitisai",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.")
        };

    private static SessionPoolKey WithOpenVinoResidency(SessionPoolKey key) =>
        key.Provider is ExecutionProviderKind.OpenVino
            ? key with { UseOpenVinoCpuProxy = _openVinoAvailability.UseOpenVinoCpuProxy }
            : key;

    private static SessionOptionsSelection CreateSessionOptions(
        ExecutionProviderKind provider,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool enableCudaGraph = false, string? logId = null)
    {
        bool useCatalogDevicePolicy = ShouldUseCatalogDevicePolicy(devicePolicy, provider);
        SessionOptions options = CreateBaseSessionOptions(
            useCatalogDevicePolicy ? devicePolicy : WindowsMlExecutionDevicePolicy.Explicit,
            out bool devicePolicyApplied,
            tensorRtRtx: provider is ExecutionProviderKind.TensorRTRtx, logId: logId);

        if (provider is ExecutionProviderKind.Cpu)
        {
            return new SessionOptionsSelection(options, ExecutionProviderKind.Cpu);
        }

        if (useCatalogDevicePolicy && IsCatalogGpuProvider(provider))
        {
            return new SessionOptionsSelection(
                options,
                provider,
                BuildDevicePolicyFallbackReason(devicePolicy, devicePolicyApplied));
        }

        return AppendProviderSpecificSelection(options, provider, additionalTrtOptions, enableCudaGraph)
            ?? throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unsupported execution provider kind.");
    }

    private static SessionOptionsSelection? AppendProviderSpecificSelection(
        SessionOptions options,
        ExecutionProviderKind provider,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        return provider switch
        {
            ExecutionProviderKind.DirectMl => CreateDirectMlSelection(options),
            ExecutionProviderKind.Dnnl => CreateDnnlSelection(options),
            ExecutionProviderKind.TensorRTRtx => CreateTensorRtRtxSelection(options, additionalTrtOptions, enableCudaGraph),
            ExecutionProviderKind.Migraphx => CreateMigraphxSelection(options),
            ExecutionProviderKind.CoreMl => CreateCoreMlSelection(options),
            ExecutionProviderKind.OpenVinoCatalog => CreateOpenVinoCatalogSelection(options),
            ExecutionProviderKind.Qnn => CreateQnnSelection(options),
            ExecutionProviderKind.VitisAi => CreateVitisAiSelection(options),
            ExecutionProviderKind.Cuda when !OperatingSystem.IsMacOS() => CreateCudaSelection(options),
            ExecutionProviderKind.TensorRt when !OperatingSystem.IsMacOS() => CreateTensorRtSelection(options, additionalTrtOptions),
#if LINML
            ExecutionProviderKind.OpenVino when OperatingSystem.IsLinux() => CreateLinuxOpenVinoSelection(options),
#endif
            _ => null,
        };
    }

    private static SessionOptionsSelection CreateDirectMlSelection(SessionOptions options)
    {
        if (!TryAppendDirectMlProvider(options, out string? catalogFailure) &&
            !TryAppendDirectMlProviderDirect(options, out string? directFailure))
        {
            return new SessionOptionsSelection(
                options,
                ExecutionProviderKind.Cpu,
                "Requested dml but DirectML append failed; CPU fallback activated. "
                + $"catalog: {catalogFailure ?? "n/a"}; direct: {directFailure ?? "n/a"}");
        }

        return new SessionOptionsSelection(options, ExecutionProviderKind.DirectMl);
    }

    private static SessionOptionsSelection CreateDnnlSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider = DnnlSessionOptionsExtensions.AppendDnnlOrFallback(
            options,
            out string? failureReason);
        return selectedProvider is ExecutionProviderKind.Dnnl
            ? new SessionOptionsSelection(options, selectedProvider)
            : new SessionOptionsSelection(
                options,
                ExecutionProviderKind.Cpu,
                $"Requested dnnl but AppendExecutionProvider_Dnnl failed: {failureReason ?? "unknown failure"}");
    }

    private static SessionOptionsSelection CreateTensorRtRtxSelection(
        SessionOptions options,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        ExecutionProviderKind selectedProvider =
            AppendTensorRtRtxOrFallbackProvider(options, additionalTrtOptions, enableCudaGraph);
        if (selectedProvider is ExecutionProviderKind.TensorRTRtx)
        {
            return new SessionOptionsSelection(options, selectedProvider);
        }

        string fallbackLabel = FormatProviderLabel(selectedProvider);
        return new SessionOptionsSelection(
            options,
            selectedProvider,
            $"Requested tensorrt-rtx but TensorRT RTX EP device was unavailable; fell back to {fallbackLabel}.");
    }

    private static SessionOptionsSelection CreateMigraphxSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider = MigraphxSessionOptionsExtensions.AppendMigraphxOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static SessionOptionsSelection CreateCoreMlSelection(SessionOptions options)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException("CoreML EP is only available on macOS.");
        options.AppendExecutionProvider_CoreML(GetCoreMlFlags());
        return new SessionOptionsSelection(options, ExecutionProviderKind.CoreMl);
    }

    private static SessionOptionsSelection CreateCudaSelection(SessionOptions options)
    {
        options.AppendExecutionProvider_CUDA(deviceId: 0);
        return new SessionOptionsSelection(options, ExecutionProviderKind.Cuda);
    }

    private static SessionOptionsSelection CreateTensorRtSelection(
        SessionOptions options,
        IReadOnlyDictionary<string, string>? additionalTrtOptions)
    {
        ExecutionProviderKind selectedProvider = AppendTensorRtOrFallbackProvider(options, additionalTrtOptions);
        if (selectedProvider is ExecutionProviderKind.TensorRt)
        {
            return new SessionOptionsSelection(options, selectedProvider);
        }

        string fallbackLabel = FormatProviderLabel(selectedProvider);
        return new SessionOptionsSelection(
            options,
            selectedProvider,
            $"Requested tensorrt but classic TensorRT EP device was unavailable; fell back to {fallbackLabel}.");
    }

#if LINML
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static SessionOptionsSelection CreateLinuxOpenVinoSelection(SessionOptions options)
    {
        if (!ExecutionProviders.LinMl.LinMlOpenVinoCatalog.Default.TryAppend(
                options, cpuOnly: _openVinoAvailability.UseOpenVinoCpuProxy, out string? failureReason))
        {
            return new SessionOptionsSelection(
                options,
                ExecutionProviderKind.Cpu,
                $"Requested openvino but the OpenVINO plugin EP append failed; CPU fallback activated. {failureReason}");
        }

        return new SessionOptionsSelection(options, ExecutionProviderKind.OpenVino);
    }
#endif

    private static SessionOptionsSelection CreateOpenVinoCatalogSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider =
            WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendOpenVinoCatalogOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static SessionOptionsSelection CreateQnnSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider =
            WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendQnnOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static SessionOptionsSelection CreateVitisAiSelection(SessionOptions options)
    {
        ExecutionProviderKind selectedProvider =
            WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendVitisAiOrFallback(options);
        return new SessionOptionsSelection(options, selectedProvider);
    }

    private static CoreMLFlags GetCoreMlFlags()
    {
        const CoreMLFlags appleSiliconFlags =
            CoreMLFlags.COREML_FLAG_ONLY_ENABLE_DEVICE_WITH_ANE |
            CoreMLFlags.COREML_FLAG_CREATE_MLPROGRAM;

        bool isAppleSilicon = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            && OperatingSystem.IsMacOS();

        return isAppleSilicon
            ? appleSiliconFlags
            : CoreMLFlags.COREML_FLAG_USE_NONE;
    }

    private static ExecutionProviderKind ResolveSharedSelectedProvider(
        ExecutionProviderKind requestedProvider,
        ExecutionProviderKind firstSessionProvider,
        ExecutionProviderKind secondSessionProvider)
    {
        if (firstSessionProvider == secondSessionProvider)
        {
            return firstSessionProvider;
        }

        return requestedProvider is ExecutionProviderKind.TensorRTRtx
            ? ExecutionProviderKind.Cpu
            : requestedProvider;
    }

    internal static ExecutionProviderKind ResolveEffectiveProviderKindFromSession(
        InferenceSession session,
        ExecutionProviderKind optionsSelectedProvider,
        bool usedCatalogDevicePolicy)
    {
        // DNNL only claims a subset of ops; ORT can silently place an unsupported graph on
        // CPU even after AppendExecutionProvider_Dnnl succeeds. Verify actual placement here
        // rather than trusting the append-time selection, mirroring DnnlReadinessProbe's
        // smoke-session check.
        if (optionsSelectedProvider is ExecutionProviderKind.Dnnl)
        {
            return ResolveDnnlEffectiveProviderFromSession(session);
        }

        // Native ORT CUDA / classic TensorRT are not WinML catalog EP names. Probing
        // GetEpDeviceForInputs through the catalog mapper would treat unmapped names as CPU.
        if (optionsSelectedProvider is ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt)
        {
            return optionsSelectedProvider;
        }

        // Linux OpenVINO is the same OpenVINOExecutionProvider plugin the Windows ML catalog
        // registers, so the catalog mapper reports it as OpenVinoCatalog.
        if (optionsSelectedProvider is ExecutionProviderKind.OpenVino)
        {
            return ResolveCatalogEffectiveProviderFromSession(session) is ExecutionProviderKind.OpenVinoCatalog
                ? ExecutionProviderKind.OpenVino
                : ExecutionProviderKind.Cpu;
        }

        // Always probe GetEpDeviceForInputs for GPU / catalog routes so TRT sessions that
        // assigned zero nodes (or fell through) report DirectML/CPU honestly.
        if (usedCatalogDevicePolicy || ShouldProbeEffectiveEpDevices(optionsSelectedProvider))
        {
            return ResolveCatalogEffectiveProviderFromSession(session);
        }

        return optionsSelectedProvider;
    }

    private static bool ShouldProbeEffectiveEpDevices(ExecutionProviderKind optionsSelectedProvider) =>
        optionsSelectedProvider is ExecutionProviderKind.TensorRTRtx
            or ExecutionProviderKind.DirectMl
            or ExecutionProviderKind.Migraphx
            or ExecutionProviderKind.Qnn
            or ExecutionProviderKind.OpenVinoCatalog
            or ExecutionProviderKind.VitisAi;

    private static ExecutionProviderKind ResolveDnnlEffectiveProviderFromSession(InferenceSession session)
    {
        try
        {
            IReadOnlyList<OrtEpDevice> devices = session.GetEpDeviceForInputs();
            return devices.Any(device => DnnlOrtProbe.IsDnnlExecutionProviderName(device.EpName))
                ? ExecutionProviderKind.Dnnl
                : ExecutionProviderKind.Cpu;
        }
        catch (OnnxRuntimeException)
        {
            return ExecutionProviderKind.Cpu;
        }
    }

    internal static ExecutionProviderKind ResolveCatalogEffectiveProviderFromSession(InferenceSession session)
    {
        IReadOnlyList<OrtEpDevice> devices;
        try
        {
            devices = session.GetEpDeviceForInputs();
        }
        catch (OnnxRuntimeException)
        {
            return ExecutionProviderKind.Cpu;
        }

        if (devices.Count == 0)
        {
            return ExecutionProviderKind.Cpu;
        }

        ExecutionProviderKind? bestProvider = null;
        int bestRank = int.MaxValue;
        foreach (OrtEpDevice device in devices)
        {
            if (!TryMapCatalogEpNameToExecutionProviderKind(device.EpName, out ExecutionProviderKind mappedProvider))
            {
                ReportUnmappedCatalogEpName(device.EpName, CatalogEpNameSource.GetEpDeviceForInputs, devices.Count);
                continue;
            }

            int rank = GetCatalogEpHonestyRank(mappedProvider, device.HardwareDevice.Type);
            if (rank >= bestRank)
            {
                continue;
            }

            bestRank = rank;
            bestProvider = mappedProvider;
        }

        return bestProvider ?? ExecutionProviderKind.Cpu;
    }

    internal static bool TryMapCatalogEpNameToExecutionProviderKind(
        string epName,
        out ExecutionProviderKind executionProviderKind)
    {
        if (IsTensorRtRtxDeviceCandidate(epName, OrtHardwareDeviceType.GPU))
        {
            executionProviderKind = ExecutionProviderKind.TensorRTRtx;
            return true;
        }

        if (string.Equals(epName, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Migraphx;
            return true;
        }

        if (IsDnnlExecutionProviderName(epName))
        {
            executionProviderKind = ExecutionProviderKind.Dnnl;
            return true;
        }

        if (IsDirectMlDeviceCandidate(epName, OrtHardwareDeviceType.GPU))
        {
            executionProviderKind = ExecutionProviderKind.DirectMl;
            return true;
        }

        if (string.Equals(epName, OpenVinoCatalogProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.OpenVinoCatalog;
            return true;
        }

        if (string.Equals(epName, QnnProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Qnn;
            return true;
        }

        if (string.Equals(epName, VitisAiProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.VitisAi;
            return true;
        }

        if (string.Equals(epName, "CPUExecutionProvider", StringComparison.OrdinalIgnoreCase))
        {
            executionProviderKind = ExecutionProviderKind.Cpu;
            return true;
        }

        executionProviderKind = default;
        return false;
    }

    internal enum CatalogEpNameSource
    {
        GetEpDeviceForInputs,
        GetEpDevices
    }

    internal static void ReportUnmappedCatalogEpName(
        string epName,
        CatalogEpNameSource source,
        int deviceCount)
    {
        if (string.IsNullOrWhiteSpace(epName))
        {
            return;
        }

        if (!WarnedUnmappedCatalogEpNames.TryAdd(epName, 0))
        {
            return;
        }

        _logger?.LogWarning(
            "Unmapped Windows ML catalog EP name '{EpName}' encountered ({Source}, deviceCount={DeviceCount}); treating as CPU only for honesty resolution. Add a mapping in TryMapCatalogEpNameToExecutionProviderKind if this EP is supported. See docs/internal/windows-ml-phase-5-catalog-eps.md.",
            epName,
            source,
            deviceCount);
    }

    private static int GetCatalogEpHonestyRank(ExecutionProviderKind provider, OrtHardwareDeviceType deviceType) =>
        (provider, deviceType) switch
        {
            (ExecutionProviderKind.TensorRTRtx, _) => 0,
            (ExecutionProviderKind.Migraphx, _) => 1,
            (ExecutionProviderKind.Qnn, OrtHardwareDeviceType.NPU) => 2,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.NPU) => 3,
            (ExecutionProviderKind.VitisAi, _) => 4,
            (ExecutionProviderKind.DirectMl, _) => 5,
            (ExecutionProviderKind.Qnn, OrtHardwareDeviceType.GPU) => 6,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.GPU) => 7,
            (ExecutionProviderKind.OpenVinoCatalog, OrtHardwareDeviceType.CPU) => 8,
            (ExecutionProviderKind.Dnnl, OrtHardwareDeviceType.CPU) => 9,
            (ExecutionProviderKind.Cpu, _) => 10,
            _ => 11
        };

    private static string? BuildEpFallbackReason(ExecutionProviderKind requestedProvider, ExecutionProviderKind effectiveProvider) =>
        effectiveProvider != requestedProvider
            ? $"Requested {FormatProviderLabel(requestedProvider)} but effective {FormatProviderLabel(effectiveProvider)}."
            : null;

    private static string? BuildSessionOptionsFallbackReason(
        ExecutionProviderKind requestedProvider,
        ExecutionProviderKind effectiveProvider,
        SessionOptionsSelection sessionOptionsSelection) =>
        MergeFallbackReasons(
            sessionOptionsSelection.FallbackReason,
            BuildEpFallbackReason(requestedProvider, effectiveProvider));

    internal static bool TryAppendDirectMlProvider(SessionOptions options, out string? failureReason)
    {
        try
        {
            AppendDirectMlProvider(options);
            failureReason = null;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    // Direct DML path — no WinML catalog/WinAppSDK bootstrap required.
    // Used when the catalog path is unavailable (e.g., test processes, headless runners).
    internal static bool TryAppendDirectMlProviderDirect(SessionOptions options, out string? failureReason)
    {
        try
        {
            options.AppendExecutionProvider_DML(deviceId: 0);
            failureReason = null;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    // EP-context artifacts are keyed by the provider options their engines were built with, so
    // session creation needs the options appended here, not a re-derivation from the model.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionOptions, IReadOnlyDictionary<string, string>>
        AppendedTensorRtRtxOptions = new();

    internal static IReadOnlyDictionary<string, string>? TryGetAppendedTensorRtRtxOptions(SessionOptions options) =>
        AppendedTensorRtRtxOptions.TryGetValue(options, out IReadOnlyDictionary<string, string>? trtOptions) ? trtOptions : null;

    internal static ExecutionProviderKind AppendTensorRtRtxOrFallbackProvider(
        SessionOptions options,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null,
        bool enableCudaGraph = false)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        var devices = OrtEnv.Instance().GetEpDevices();
        var trtDevice = devices.FirstOrDefault(d => IsTensorRtRtxDeviceCandidate(d.EpName, d.HardwareDevice.Type));
        if (trtDevice != null)
        {
            IReadOnlyDictionary<string, string> trtOptions = BuildTensorRtRtxOptions(additionalTrtOptions, enableCudaGraph);
            options.AppendExecutionProvider(OrtEnv.Instance(), new[] { trtDevice }, trtOptions);
            AppendedTensorRtRtxOptions.AddOrUpdate(options, trtOptions);
            return ExecutionProviderKind.TensorRTRtx;
        }

        if (_logger?.IsEnabled(LogLevel.Debug) == true)
        {
            foreach (OrtEpDevice device in devices.Where(d => d.HardwareDevice.Type is OrtHardwareDeviceType.GPU))
            {
                if (!TryMapCatalogEpNameToExecutionProviderKind(device.EpName, out _))
                {
                    ReportUnmappedCatalogEpName(device.EpName, CatalogEpNameSource.GetEpDevices, devices.Count);
                }
            }
        }

        // TensorRT RTX plugin device not available; fallback to DirectML if possible, else CPU is default.
        return TryAppendDirectMlProvider(options, out _)
            ? ExecutionProviderKind.DirectMl
            : ExecutionProviderKind.Cpu;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SessionOptions, IReadOnlyDictionary<string, string>>
        AppendedTensorRtOptions = new();

    internal static IReadOnlyDictionary<string, string>? TryGetAppendedTensorRtOptions(SessionOptions options) =>
        AppendedTensorRtOptions.TryGetValue(options, out IReadOnlyDictionary<string, string>? trtOptions) ? trtOptions : null;

    internal static bool IsTensorRtDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        string.Equals(epName, ClassicTensorRtExecutionProviderName, StringComparison.Ordinal);

    /// <summary>
    /// Appends the classic TensorRT EP with the persistent engine-cache options from
    /// <see cref="BuildTensorRtOptions"/>. Prefers the generic EP-device append when the
    /// classic TRT device is enumerated by <c>OrtEnv.GetEpDevices()</c>; otherwise uses the
    /// built-in provider-options append (<see cref="OrtTensorRTProviderOptions"/>), because
    /// the built-in EP is reported by <c>GetAvailableProviders()</c> but is NOT enumerated
    /// as an EP device (verified live on ORT 1.31 in the GPU container: only the CPU device
    /// is enumerated), so the generic branch is normally unreachable for classic TRT. There is
    /// no TensorRT append without the cache options: when the provider-options append fails,
    /// this falls back to DirectML/CPU so callers can report the fallback instead of claiming
    /// a cached TensorRT session.
    /// </summary>
    internal static ExecutionProviderKind AppendTensorRtOrFallbackProvider(
        SessionOptions options,
        IReadOnlyDictionary<string, string>? additionalTrtOptions = null)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        var devices = OrtEnv.Instance().GetEpDevices();
        var trtDevice = devices.FirstOrDefault(d => IsTensorRtDeviceCandidate(d.EpName, d.HardwareDevice.Type));
        if (trtDevice != null)
        {
            IReadOnlyDictionary<string, string> trtOptions = BuildTensorRtOptions(additionalTrtOptions);
            EnsureClassicTensorRtCacheDirectory(trtOptions);
            options.AppendExecutionProvider(OrtEnv.Instance(), new[] { trtDevice }, trtOptions);
            AppendedTensorRtOptions.AddOrUpdate(options, trtOptions);
            return ExecutionProviderKind.TensorRt;
        }

        try
        {
            IReadOnlyDictionary<string, string> trtOptions = BuildTensorRtOptions(additionalTrtOptions);
            using OrtTensorRTProviderOptions providerOptions = BuildTensorRtProviderOptions(trtOptions);
            options.AppendExecutionProvider_Tensorrt(providerOptions);
            AppendedTensorRtOptions.AddOrUpdate(options, trtOptions);
            return ExecutionProviderKind.TensorRt;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // Built-in TensorRT EP provider-options append is not usable; try the legacy append.
        }

        return TryAppendDirectMlProvider(options, out _)
            ? ExecutionProviderKind.DirectMl
            : ExecutionProviderKind.Cpu;
    }

    internal static OrtTensorRTProviderOptions BuildTensorRtProviderOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        string environmentFingerprint) =>
        BuildTensorRtProviderOptions(BuildTensorRtOptions(additionalTrtOptions, environmentFingerprint));

    internal static OrtTensorRTProviderOptions BuildTensorRtProviderOptions(
        IReadOnlyDictionary<string, string> trtOptions)
    {
        EnsureClassicTensorRtCacheDirectory(trtOptions);
        var providerOptions = new OrtTensorRTProviderOptions();
        providerOptions.UpdateOptions(new Dictionary<string, string>(trtOptions, StringComparer.Ordinal));
        return providerOptions;
    }

    private static void EnsureClassicTensorRtCacheDirectory(IReadOnlyDictionary<string, string> trtOptions)
    {
        if (trtOptions.TryGetValue("trt_engine_cache_path", out string? cachePath) &&
            !string.IsNullOrWhiteSpace(cachePath))
        {
            Directory.CreateDirectory(cachePath);
        }
    }

    internal static void AppendDirectMlProvider(SessionOptions options)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        var devices = OrtEnv.Instance().GetEpDevices();
        OrtEpDevice? directMlDevice = devices.FirstOrDefault(d => IsDirectMlDeviceCandidate(d.EpName, d.HardwareDevice.Type))
            ?? devices.FirstOrDefault(d => IsDirectMlExecutionProviderName(d.EpName));
        if (directMlDevice is not null)
        {
            options.AppendExecutionProvider(
                OrtEnv.Instance(),
                new[] { directMlDevice },
                new Dictionary<string, string>(StringComparer.Ordinal));
            return;
        }

        try
        {
            options.AppendExecutionProvider(DirectMlExecutionProviderName);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException(
                "DirectML catalog execution provider is not visible in OrtEnv.GetEpDevices().",
                ex);
        }
    }

    internal sealed record SessionOptionsSelection(
        SessionOptions Options,
        ExecutionProviderKind SelectedProvider,
        string? FallbackReason = null);

    internal static bool IsTensorRtRtxDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        // Only accept the single canonical standalone EP ABI plugin name.
        // "NvTensorRtExecutionProvider" and "TensorrtExecutionProvider" are the old CUDA-based TensorRT EP —
        // they must NOT be treated as TRT RTX candidates (different EP family, different options).
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        string.Equals(epName, TensorRtRtxExecutionProviderName, StringComparison.Ordinal);

    private static bool IsDirectMlExecutionProviderName(string epName) =>
        string.Equals(epName, DirectMlExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(epName, DirectMlLongExecutionProviderName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDirectMlDeviceCandidate(string epName, OrtHardwareDeviceType hardwareDeviceType) =>
        hardwareDeviceType is OrtHardwareDeviceType.GPU &&
        IsDirectMlExecutionProviderName(epName);

    private static bool IsDnnlExecutionProviderName(string epName) =>
        string.Equals(epName, DnnlExecutionProviderName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(epName, DnnlUpperExecutionProviderName, StringComparison.OrdinalIgnoreCase);

    // Classic TensorRT EP uses trt_profile_*. NvTensorRTRTX rejects those keys.
    // Same shape grammar: "input:dim1xdim2x...,input2:..."
    // https://onnxruntime.ai/docs/execution-providers/TensorRTRTX-ExecutionProvider.html
    private static readonly IReadOnlyDictionary<string, string> ClassicTrtProfileKeysToNvRtx =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["trt_profile_min_shapes"] = "nv_profile_min_shapes",
            ["trt_profile_max_shapes"] = "nv_profile_max_shapes",
            ["trt_profile_opt_shapes"] = "nv_profile_opt_shapes",
        };

    /// <summary>
    /// Builds TensorRT-RTX provider options.
    /// </summary>
    /// <param name="enableCudaGraph">
    /// CUDA graph capture replays a fixed sequence of GPU kernel launches against fixed device
    /// memory addresses (see ONNX Runtime's TensorRT-RTX EP docs: "Avoid enabling CUDA Graph ...
    /// if input shapes or device bindings frequently change"). Defaults to <see langword="false"/>
    /// because most call sites build fresh input tensors on every <c>Run()</c>, which violates that
    /// precondition and can silently replay stale device buffers. Pass <see langword="true"/> only
    /// for a session whose call site is verified to feed stable, IoBinding-backed device buffers
    /// across iterations.
    /// </param>
    internal static IReadOnlyDictionary<string, string> BuildTensorRtRtxOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        var trtOptions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Cache compiled TRT-RTX kernels locally to speed up subsequent session loads.
            ["nv_runtime_cache_path"] = ResolveTensorRtRtxRuntimeCachePath(),
        };

        // Always write the flag explicitly: the TensorRT RTX EP defaults enable_cuda_graph to
        // true, so omitting it leaves capture on rather than off.
        trtOptions["enable_cuda_graph"] = enableCudaGraph ? "1" : "0";

        if (additionalTrtOptions is null)
        {
            return trtOptions;
        }

        foreach ((string key, string value) in additionalTrtOptions)
        {
            if (ClassicTrtProfileKeysToNvRtx.ContainsKey(key))
            {
                continue;
            }

            trtOptions[key] = value;
        }

        foreach ((string classicKey, string nvKey) in ClassicTrtProfileKeysToNvRtx)
        {
            if (trtOptions.ContainsKey(nvKey))
            {
                continue;
            }

            if (additionalTrtOptions.TryGetValue(classicKey, out string? shapes))
            {
                trtOptions[nvKey] = shapes;
            }
        }

        return trtOptions;
    }

    /// <summary>
    /// Builds classic TensorRT EP provider options with the persistent engine cache enabled.
    /// Caller options (e.g. <c>trt_profile_*</c>) pass through, but the cache keys are
    /// authoritative: a caller must not be able to disable the cache or redirect it outside
    /// the fingerprinted directory, so they are written after the caller options.
    /// ORT option keys: <c>trt_engine_cache_enable</c> / <c>trt_engine_cache_path</c>
    /// (ONNX Runtime TensorRT execution provider documentation).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildTensorRtOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions) =>
        BuildTensorRtOptions(additionalTrtOptions, BuildClassicTensorRtEnvironmentFingerprint());

    internal static IReadOnlyDictionary<string, string> BuildTensorRtOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        string environmentFingerprint)
    {
        var trtOptions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (additionalTrtOptions is not null)
        {
            foreach ((string key, string value) in additionalTrtOptions)
            {
                // Caller options are shared with the TensorRT-RTX builder. The classic EP rejects
                // unknown keys, which makes UpdateOptions throw and drops the session to no-cache.
                if (string.Equals(key, "enable_cuda_graph", StringComparison.Ordinal))
                {
                    trtOptions["trt_cuda_graph_enable"] = value;
                }
                else if (!key.StartsWith("nv_", StringComparison.Ordinal))
                {
                    trtOptions[key] = value;
                }
            }
        }

        trtOptions["trt_engine_cache_enable"] = "1";
        trtOptions["trt_engine_cache_path"] = ResolveClassicTensorRtEngineCacheDirectory(environmentFingerprint);
        return trtOptions;
    }

    internal static string ResolveClassicTensorRtEngineCacheDirectory() =>
        ResolveClassicTensorRtEngineCacheDirectory(BuildClassicTensorRtEnvironmentFingerprint());

    internal static string ResolveClassicTensorRtEngineCacheDirectory(string environmentFingerprint) =>
        Path.Join(
            ResolveTensorRtRtxRuntimeCachePath(),
            "classic-trt",
            SanitizeClassicTensorRtCacheKey(environmentFingerprint));

    // Classic engine-cache key components: GPU architecture bucket (the hardware probe's
    // NvidiaGpuArchitecture name) | GPU model name | GPU driver version | TensorRT version
    // identity | ONNX Runtime version. The model name keeps engines from being reused across two
    // GPUs in the same architecture bucket (e.g. a moved /data volume); it is "unknown" where NVML
    // is unavailable. The TensorRT-RTX EP version in EpContextLoadPathResolver's fingerprint is
    // deliberately excluded — classic TRT engines do not depend on it. TensorRT identity is sourced
    // from the native library the EP links: the libnvinfer soname version on Linux (e.g.
    // libnvinfer.so.10.13.3), the nvinfer.dll file version on Windows, and "unknown" when no library
    // is discoverable in-process.
    internal static string BuildClassicTensorRtEnvironmentFingerprint()
    {
        (string gpuArchitecture, string? driverVersion) = EpContextLoadPathResolver.CurrentHardware;
        string driver = string.IsNullOrWhiteSpace(driverVersion) ? "unknown" : driverVersion;
        string gpuModel = AcceleratorVramProbe.QueryNvmlGpuIdentity().DeviceName ?? "unknown";
        return $"{gpuArchitecture}|{gpuModel}|{driver}|trt-{ResolveTensorRtVersionIdentity()}|ort-{EpContextArtifact.HostOrtRuntimeVersion}";
    }

    private static string SanitizeClassicTensorRtCacheKey(string environmentFingerprint)
    {
        var builder = new System.Text.StringBuilder(environmentFingerprint.Length);
        foreach (char c in environmentFingerprint)
        {
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-');
        }

        string sanitized = builder.ToString().Trim('-');
        return sanitized.Length == 0 ? "unknown" : sanitized;
    }

    internal static string ParseTensorRtVersionFromLibraryFileName(string fileName)
    {
        string name = Path.GetFileName(fileName);
        const string sonamePrefix = "libnvinfer.so.";
        if (name.StartsWith(sonamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            string version = name[sonamePrefix.Length..].Trim();
            return version.Length == 0 ? "unknown" : version;
        }

        return "unknown";
    }

    internal static string ResolveTensorRtVersionIdentity()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                string? pathVariable = Environment.GetEnvironmentVariable("PATH");
                if (!string.IsNullOrWhiteSpace(pathVariable))
                {
                    foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(static d => d.Trim()))
                    {
                        string candidate = Path.Join(directory, "nvinfer.dll");
                        if (!File.Exists(candidate))
                        {
                            continue;
                        }

                        FileVersionInfo info = FileVersionInfo.GetVersionInfo(candidate);
                        string? version = !string.IsNullOrWhiteSpace(info.FileVersion) ? info.FileVersion : info.ProductVersion;
                        if (!string.IsNullOrWhiteSpace(version))
                        {
                            return version.Trim();
                        }
                    }
                }

                return "unknown";
            }

            if (OperatingSystem.IsLinux())
            {
                string? best = null;
                foreach (string directory in EnumerateTensorRtLibrarySearchDirectories())
                {
                    if (!Directory.Exists(directory))
                    {
                        continue;
                    }

                    foreach (string version in Directory.EnumerateFiles(directory, "libnvinfer.so*", SearchOption.TopDirectoryOnly)
                        .Select(ParseTensorRtVersionFromLibraryFileName))
                    {
                        if (version != "unknown" && (best is null || IsNewerTensorRtVersion(version, best)))
                        {
                            best = version;
                        }
                    }
                }

                return best ?? "unknown";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return "unknown";
        }

        return "unknown";
    }

    // Compares dotted numeric versions component-wise so "10.13.3" outranks "10" and
    // "10.13.3.0.0" outranks "10.13.3" only by its numeric components, not by string length.
    // Ordinal string order breaks ties so the selection does not depend on directory enumeration order.
    private static bool IsNewerTensorRtVersion(string candidate, string current)
    {
        string[] candidateParts = candidate.Split('.');
        string[] currentParts = current.Split('.');
        for (int i = 0; i < Math.Max(candidateParts.Length, currentParts.Length); i++)
        {
            int candidateValue = ParseVersionComponent(candidateParts, i);
            int currentValue = ParseVersionComponent(currentParts, i);
            if (candidateValue != currentValue)
            {
                return candidateValue > currentValue;
            }
        }

        return string.CompareOrdinal(candidate, current) > 0;
    }

    private static int ParseVersionComponent(string[] parts, int index) =>
        index < parts.Length && int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;

    private static IEnumerable<string> EnumerateTensorRtLibrarySearchDirectories()
    {
        yield return "/usr/lib/x86_64-linux-gnu";
        yield return "/usr/lib/aarch64-linux-gnu";
        yield return "/usr/local/cuda/lib";
        yield return "/usr/local/cuda/lib64";
        yield return "/usr/lib";

        string? ldLibraryPath = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
        if (string.IsNullOrWhiteSpace(ldLibraryPath))
        {
            yield break;
        }

        foreach (string path in ldLibraryPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            yield return path;
        }
    }

    /// <summary>
    /// Composes enable_cuda_graph onto a caller-supplied TRT option set. Production callsites that
    /// keep every bound OrtValue at a fixed address opt in here; profile dictionaries themselves
    /// stay free of the flag so benchmark/AOT consumers keep explicit control of the session mode.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> WithCudaGraphsEnabled(
        IReadOnlyDictionary<string, string> options)
    {
        var merged = new Dictionary<string, string>(options, StringComparer.Ordinal)
        {
            ["enable_cuda_graph"] = "1",
        };
        return merged;
    }

    private static string ResolveTensorRtRtxRuntimeCachePath()
    {
        string? engineCacheRoot = Environment.GetEnvironmentVariable(EngineCacheRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(engineCacheRoot))
        {
            return NormalizePath(engineCacheRoot);
        }

        string? cacheRoot = Environment.GetEnvironmentVariable(CacheRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(cacheRoot))
        {
            return Path.Join(NormalizePath(cacheRoot), "EngineCache");
        }

        string localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            localAppDataRoot = AppContext.BaseDirectory;
        }

        return Path.Join(localAppDataRoot, "Trackdub", "EngineCache");
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    private static string BuildSessionOptionsFingerprint(
        ExecutionProviderKind selectedProviderKind,
        WindowsMlExecutionDevicePolicy devicePolicy,
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        bool enableCudaGraph = false)
    {
        if (selectedProviderKind is ExecutionProviderKind.TensorRTRtx)
        {
            var combinedOptions = new Dictionary<string, string>(
                BuildTensorRtRtxOptions(additionalTrtOptions, enableCudaGraph), StringComparer.Ordinal);

            return SessionPoolKey.HashOptions(combinedOptions);
        }

        if (selectedProviderKind is ExecutionProviderKind.TensorRt)
        {
            var combinedOptions = new Dictionary<string, string>(
                BuildTensorRtOptions(additionalTrtOptions), StringComparer.Ordinal);

            return SessionPoolKey.HashOptions(combinedOptions);
        }

        if (ShouldIncludePolicyInFingerprint(devicePolicy, selectedProviderKind))
        {
            return SessionPoolKey.HashOptions(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["windows_ml_device_policy"] = WindowsMlExecutionDevicePolicySettings.ToKey(devicePolicy)
            });
        }

        return SessionPoolKey.HashOptions(null);
    }

    private static bool ShouldIncludePolicyInFingerprint(
        WindowsMlExecutionDevicePolicy devicePolicy,
        ExecutionProviderKind selectedProviderKind) =>
        ShouldUseCatalogDevicePolicy(devicePolicy, selectedProviderKind);

    private static string? MergeFallbackReasons(string? encoder, string? decoder) =>
        (encoder, decoder) switch
        {
            (null, null) => null,
            ({ } e, null) => e,
            (null, { } d) => d,
            ({ } e, { } d) when e == d => e,
            ({ } e, { } d) => $"Encoder: {e} Decoder: {d}"
        };

    private static string? FormatBootstrapDetail(
        string? bootstrapDetail,
        string? sessionOptionsFallbackReason)
    {
        if (string.IsNullOrWhiteSpace(sessionOptionsFallbackReason))
        {
            return bootstrapDetail;
        }

        return string.IsNullOrWhiteSpace(bootstrapDetail)
            ? $"Session options fallback reason: {sessionOptionsFallbackReason}"
            : $"{bootstrapDetail} Session options fallback reason: {sessionOptionsFallbackReason}";
    }

    private static SessionOptions CreateBaseSessionOptions(
        WindowsMlExecutionDevicePolicy devicePolicy,
        out bool devicePolicyApplied,
        bool tensorRtRtx = false, string? logId = null)
    {
        SessionOptions options = new()
        {
            // ORT_ENABLE_ALL re-fuses LayerNormalization+Skip / Bias+Gelu into com.microsoft
            // contrib ops (SkipLayerNormalization, BiasGelu) at graph resolve. TensorRT-RTX
            // cannot import those, so the TRT session gets a parse-error storm and falls back.
            // BASIC keeps standard Add / LayerNormalization / Gelu nodes the TRT-RTX parser accepts.
            GraphOptimizationLevel = tensorRtRtx
                ? GraphOptimizationLevel.ORT_ENABLE_BASIC
                : GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogId = logId ?? string.Empty,
            // MS "Efficiency tips for Windows ML inference" memory lever: opt-in arena disable
            // for memory-footprint-sensitive deployments (throughput tradeoff). Default: arena on.
            EnableCpuMemArena = !Common.SessionEfficiencyEnvironment.DisableCpuMemArena
        };

        devicePolicyApplied = false;
#if WINDOWS
        devicePolicyApplied = WindowsMlExecutionDevicePolicyMapper.ApplyIfNeeded(options, devicePolicy);
#endif
        return options;
    }

    /// <summary>
    /// Reports the truth about whether a requested extended Windows ML device policy
    /// (<see cref="WindowsMlExecutionDevicePolicy.DefaultRender"/> /
    /// <see cref="WindowsMlExecutionDevicePolicy.MinPower"/>) actually took effect on the
    /// session. <see cref="WindowsMlExecutionDevicePolicyMapper.ApplyIfNeeded"/> silently skips
    /// <c>SetEpSelectionPolicy</c> when the loaded ORT managed binding lacks the required enum
    /// members, so callers must not report the policy as active without checking
    /// <paramref name="devicePolicyApplied"/> — otherwise diagnostics/fingerprints would claim a
    /// device-selection behavior that never actually happened.
    /// </summary>
    private static string? BuildDevicePolicyFallbackReason(
        WindowsMlExecutionDevicePolicy devicePolicy,
        bool devicePolicyApplied) =>
        !devicePolicyApplied &&
        devicePolicy is WindowsMlExecutionDevicePolicy.DefaultRender or WindowsMlExecutionDevicePolicy.MinPower
            ? $"Requested Windows ML device policy '{WindowsMlExecutionDevicePolicySettings.ToKey(devicePolicy)}' " +
              "was not applied: the loaded ONNX Runtime managed binding does not expose the required " +
              "extended ExecutionProviderDevicePolicy members (DEFAULT_RENDER/MIN_POWER); the session " +
              "falls back to Explicit EP selection instead."
            : null;

    private static async Task<WindowsMlExecutionDevicePolicy> ResolveDevicePolicyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _devicePolicyProvider.GetPolicyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return WindowsMlExecutionDevicePolicy.Explicit;
        }
    }

    private static bool IsCatalogGpuProvider(ExecutionProviderKind provider) =>
        provider is ExecutionProviderKind.DirectMl
            or ExecutionProviderKind.Migraphx;

    private static bool IsNativeGpuProvider(ExecutionProviderKind provider) =>
        !OperatingSystem.IsMacOS() && provider is (ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRt);

    internal static bool ShouldUseCatalogDevicePolicy(
        WindowsMlExecutionDevicePolicy devicePolicy,
        ExecutionProviderKind provider) =>
        OperatingSystem.IsWindows() &&
        devicePolicy != WindowsMlExecutionDevicePolicy.Explicit &&
        IsWindowsMlCatalogProvider(provider);

    /// <summary>
    /// WinML catalog EPs that can take ORT <c>SetEpSelectionPolicy</c> (device auto-select among
    /// registered catalog devices). DirectML stays on the legacy explicit-append path; native
    /// CUDA/TensorRT/TensorRT RTX/DNNL/CoreML are not Windows ML catalog routes.
    /// </summary>
    private static bool IsWindowsMlCatalogProvider(ExecutionProviderKind provider) =>
        provider is ExecutionProviderKind.Migraphx
            or ExecutionProviderKind.Qnn
            or ExecutionProviderKind.VitisAi
            or ExecutionProviderKind.OpenVinoCatalog;

    private sealed class NullWindowsMlEpDevicePolicyProvider : IWindowsMlEpDevicePolicyProvider
    {
        internal static readonly NullWindowsMlEpDevicePolicyProvider Instance = new();

        public Task<WindowsMlExecutionDevicePolicy> GetPolicyAsync(CancellationToken cancellationToken = default) =>
            cancellationToken.IsCancellationRequested
                ? Task.FromCanceled<WindowsMlExecutionDevicePolicy>(cancellationToken)
                : Task.FromResult(WindowsMlExecutionDevicePolicy.Explicit);

        public void InvalidateCache()
        {
        }
    }

    internal static IExecutionProviderBootstrapper GetPlatformBootstrapper()
    {
#if WINDOWS
        return new ExecutionProviders.Windows.WindowsExecutionProviderBootstrapper();
#else
        if (OperatingSystem.IsMacOSVersionAtLeast(10, 15))
        {
            return new ExecutionProviders.Mac.MacExecutionProviderBootstrapper();
        }

        if (OperatingSystem.IsLinux())
        {
            return new ExecutionProviders.Linux.LinuxExecutionProviderBootstrapper(
                NullOpenVinoAvailabilityProvider.Instance);
        }

        return new ExecutionProviders.PortableExecutionProviderBootstrapper(
            TensorRtRtx.TensorRtRtxProviderBootstrapFactory.CreateWithDefaultInstallPath(
                Path.Join(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Trackdub")));
#endif
    }

    private sealed class NullOpenVinoAvailabilityProvider : IOpenVinoAvailabilityProvider
    {
        public static readonly NullOpenVinoAvailabilityProvider Instance = new();
        public bool IsAvailable => false;
        public bool UseOpenVinoCpuProxy => false;
    }

    internal sealed record SingleSessionLease(
        InferenceSession Session,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? PoolLease { get; init; }

        /// <summary>Set when the lease came from the shared pool — used for residency pinning.</summary>
        internal SessionPoolKey? ResolvedPoolKey { get; init; }

        public void Dispose()
        {
            if (PoolLease is not null)
            {
                PoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(Session);
            }
        }
    }

    /// <summary>
    /// Engine-lifetime residency pin (audit §3A). Keeps the pooled session warm without
    /// holding an execution lease; call <see cref="AcquireAsync"/> only around each Run().
    /// </summary>
    internal sealed class PooledSingleSessionPin : IDisposable
    {
        private readonly SessionResidency residency;
        private readonly Func<CancellationToken, Task<SingleSessionLease>> acquire;
        private readonly string requestedProvider;
        private readonly string selectedProvider;
        private readonly string? bootstrapDetail;

        internal PooledSingleSessionPin(
            SessionResidency residency,
            Func<CancellationToken, Task<SingleSessionLease>> acquire,
            string requestedProvider,
            string selectedProvider,
            string? bootstrapDetail)
        {
            this.residency = residency;
            this.acquire = acquire;
            this.requestedProvider = requestedProvider;
            this.selectedProvider = selectedProvider;
            this.bootstrapDetail = bootstrapDetail;
        }

        public string RequestedProvider => requestedProvider;
        public string SelectedProvider => selectedProvider;
        public string? BootstrapDetail => bootstrapDetail;

        public Task<SingleSessionLease> AcquireAsync(CancellationToken cancellationToken) =>
            acquire(cancellationToken);

        public void Dispose() => residency.Dispose();
    }

    internal sealed record Qwen3AsrSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderInitSession,
        InferenceSession DecoderStepSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLeaseBundle? PoolBundle { get; init; }
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderInitPoolLease { get; init; }
        internal SessionLease? DecoderStepPoolLease { get; init; }

        public void Dispose()
        {
            if (PoolBundle is not null)
            {
                PoolBundle.Dispose();
                return;
            }

            if (EncoderPoolLease is not null)
            {
                EncoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(EncoderSession);
            }

            if (DecoderInitPoolLease is not null)
            {
                DecoderInitPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(DecoderInitSession);
            }

            if (DecoderStepPoolLease is not null)
            {
                DecoderStepPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(DecoderStepSession);
            }
        }
    }

    internal sealed record LatentSyncSessionLease(
        InferenceSession UNetSession,
        InferenceSession VaeEncoderSession,
        InferenceSession VaeDecoderSession,
        InferenceSession WhisperEncoderSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLeaseBundle? PoolBundle { get; init; }
        internal SessionLease? UNetPoolLease { get; init; }
        internal SessionLease? VaeEncoderPoolLease { get; init; }
        internal SessionLease? VaeDecoderPoolLease { get; init; }
        internal SessionLease? WhisperEncoderPoolLease { get; init; }

        public void Dispose()
        {
            if (PoolBundle is not null)
            {
                PoolBundle.Dispose();
                return;
            }

            if (UNetPoolLease is not null)
            {
                UNetPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(UNetSession);
            }

            if (VaeEncoderPoolLease is not null)
            {
                VaeEncoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(VaeEncoderSession);
            }

            if (VaeDecoderPoolLease is not null)
            {
                VaeDecoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(VaeDecoderSession);
            }

            if (WhisperEncoderPoolLease is not null)
            {
                WhisperEncoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(WhisperEncoderSession);
            }
        }
    }

    internal sealed record NemotronAsrSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderJointSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderJointPoolLease { get; init; }

        public void Dispose()
        {
            if (EncoderPoolLease is not null)
            {
                EncoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(EncoderSession);
            }

            if (DecoderJointPoolLease is not null)
            {
                DecoderJointPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(DecoderJointSession);
            }
        }
    }

    internal sealed record WhisperSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderPoolLease { get; init; }

        public void Dispose()
        {
            if (EncoderPoolLease is not null)
            {
                EncoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(EncoderSession);
            }

            if (DecoderPoolLease is not null)
            {
                DecoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(DecoderSession);
            }
        }
    }

    internal sealed record OpusSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderSession,
        string RequestedProvider,
        string SelectedProvider,
        string? BootstrapDetail) : IDisposable
    {
        /// <summary>Provider the sessions actually run on, after any downgrade.</summary>
        internal ExecutionProviderKind SelectedProviderKind { get; init; } = ExecutionProviderKind.Cpu;

        internal SessionLease? EncoderPoolLease { get; init; }
        internal SessionLease? DecoderPoolLease { get; init; }

        public void Dispose()
        {
            if (EncoderPoolLease is not null)
            {
                EncoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(EncoderSession);
            }

            if (DecoderPoolLease is not null)
            {
                DecoderPoolLease.Dispose();
            }
            else
            {
                TensorRtRtxTeardownGuard.DisposeSafely(DecoderSession);
            }
        }
    }
}
