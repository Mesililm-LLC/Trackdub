using System.Diagnostics;
using System.Text.Json;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
using Trackdub.Inference;
using Trackdub.Inference.Onnx.Migraphx;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.NativeCudaTensorRt;
using Trackdub.Inference.Runtime.TensorRtRtx;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx;

public sealed class OnnxModelBenchmarkRunner : IModelBenchmarkRunner
{
    private readonly ITensorRtRtxProviderBootstrap _tensorRtRtxProviderBootstrap;
    private static readonly AsyncLocal<WindowsMlExecutionDevicePolicy?> RunWindowsMlDevicePolicy = new();

    public OnnxModelBenchmarkRunner()
        : this(TensorRtRtxProviderBootstrapFactory.Shared)
    {
    }

    public OnnxModelBenchmarkRunner(ITensorRtRtxProviderBootstrap tensorRtRtxProviderBootstrap)
    {
        _tensorRtRtxProviderBootstrap = tensorRtRtxProviderBootstrap
            ?? throw new ArgumentNullException(nameof(tensorRtRtxProviderBootstrap));
    }

    private static WindowsMlExecutionDevicePolicy ActiveWindowsMlDevicePolicy =>
        RunWindowsMlDevicePolicy.Value ?? WindowsMlExecutionDevicePolicy.Explicit;

    private static WindowsMlExecutionDevicePolicy ResolveWindowsMlDevicePolicy(BenchmarkRequest request) =>
        string.IsNullOrWhiteSpace(request.WindowsMlDevicePolicyKey)
            ? WindowsMlExecutionDevicePolicy.Explicit
            : WindowsMlExecutionDevicePolicySettings.FromKey(request.WindowsMlDevicePolicyKey);

    public async Task<BenchmarkReport> RunAsync(BenchmarkRequest request, CancellationToken cancellationToken)
    {
#if WINDOWS
        WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
        cancellationToken.ThrowIfCancellationRequested();

        RunWindowsMlDevicePolicy.Value = ResolveWindowsMlDevicePolicy(request);
        try
        {
            return await RunCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            RunWindowsMlDevicePolicy.Value = null;
        }
    }

    private async Task<BenchmarkReport> RunCoreAsync(BenchmarkRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fullModelPath = request.ModelPath;
        var fullReportPath = Path.GetFullPath(request.ReportPath);
        var requestedProvider = FormatProviderPreference(request.ProviderPreference);
        var selectedProvider = requestedProvider;
        var modelSizeBytes = File.Exists(fullModelPath) ? new FileInfo(fullModelPath).Length : 0L;
        var notes = new List<string>();
#if WINDOWS
        notes.Add($"ORT package version: {typeof(OrtEnv).Assembly.GetName().Version}");
#endif

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            fullModelPath = ResolveModelPath(fullModelPath, notes);
            await TryRegisterWindowsMlProvidersAsync(request.ProviderPreference, notes, cancellationToken).ConfigureAwait(false);

            modelSizeBytes = new FileInfo(fullModelPath).Length;
            notes.Add($"Model file discovered at '{fullModelPath}'.");

            BenchmarkExecution execution = LooksLikeWhisperEncoder(fullModelPath)
                ? RunWhisperExecution(fullModelPath, request.ProviderPreference, request.RunCount, notes)
                : LooksLikeOpusEncoder(fullModelPath)
                    ? RunOpusExecution(fullModelPath, request.ProviderPreference, request.RunCount, notes)
                : RunSingleSessionExecution(fullModelPath, request.ProviderPreference, request.RunCount, notes);

            requestedProvider = execution.RequestedProvider;
            selectedProvider = execution.SelectedProvider;
            modelSizeBytes = execution.ModelSizeBytes;

            var realTimeFactorAverage = CalculateRealTimeFactorAverage(
                execution.AudioDurationSeconds,
                execution.WarmLatencyAverageMilliseconds);

            var measurements = new BenchmarkMeasurements(
                ColdLoadMilliseconds: execution.ColdLoadMilliseconds,
                WarmupMilliseconds: execution.WarmupMilliseconds,
                WarmLatencyAverageMilliseconds: execution.WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds: execution.WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds: execution.WarmLatencyMaximumMilliseconds,
                AudioDurationSeconds: execution.AudioDurationSeconds,
                RealTimeFactorAverage: realTimeFactorAverage,
                EngineCacheOutcome: execution.EngineCacheOutcome,
                EngineCacheBytesAdded: execution.EngineCacheBytesAdded,
                EngineCacheFilesAdded: execution.EngineCacheFilesAdded,
                ColdLoadDominantPhase: execution.ColdLoadDominantPhase);

            return CreateReport(
                scenario: execution.Scenario,
                status: BenchmarkStatus.Completed,
                modelPath: fullModelPath,
                reportPath: fullReportPath,
                requestedProvider: requestedProvider,
                selectedProvider: selectedProvider,
                runCount: request.RunCount,
                supportsExecution: true,
                modelSizeBytes: modelSizeBytes,
                measurements: measurements,
                failureReason: null,
                notes: notes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            notes.Add($"Benchmark failed: {ex.GetType().Name}");
            return CreateReport(
                scenario: ResolveFailureScenario(fullModelPath),
                status: BenchmarkStatus.Failed,
                modelPath: fullModelPath,
                reportPath: fullReportPath,
                requestedProvider: requestedProvider,
                selectedProvider: selectedProvider,
                runCount: request.RunCount,
                supportsExecution: false,
                modelSizeBytes: modelSizeBytes,
                measurements: EmptyMeasurements(),
                failureReason: ex.Message,
                notes: notes);
        }
    }

    private static ExecutionProviderKind MapPreferenceToExecutionProvider(BenchmarkProviderPreference preference) =>
        preference switch
        {
            BenchmarkProviderPreference.Cpu => ExecutionProviderKind.Cpu,
            BenchmarkProviderPreference.Dml => ExecutionProviderKind.DirectMl,
            BenchmarkProviderPreference.TensorRtRtx => ExecutionProviderKind.TensorRTRtx,
            BenchmarkProviderPreference.Migraphx => ExecutionProviderKind.Migraphx,
            BenchmarkProviderPreference.Cuda => ExecutionProviderKind.Cuda,
            BenchmarkProviderPreference.TensorRt => ExecutionProviderKind.TensorRt,
            BenchmarkProviderPreference.Qnn => ExecutionProviderKind.Qnn,
            BenchmarkProviderPreference.Auto => ExecutionProviderKind.DirectMl,
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown provider preference.")
        };

    private async Task TryRegisterWindowsMlProvidersAsync(
        BenchmarkProviderPreference preference,
        ICollection<string> notes,
        CancellationToken cancellationToken)
    {
        if (preference is BenchmarkProviderPreference.Cuda or BenchmarkProviderPreference.TensorRt)
        {
            notes.Add("Skipping WinML catalog registration for native ORT CUDA/TensorRT benchmark.");
            return;
        }

        ExecutionProviderKind provider = MapPreferenceToExecutionProvider(preference);

        if (provider is ExecutionProviderKind.Migraphx)
        {
            var catalog = new WindowsMlMigraphxCatalogService();
            MigraphxBootstrapResult bootstrap = await catalog
                .EnsureRegisteredAsync(allowProviderDownloads: true, cancellationToken)
                .ConfigureAwait(false);
            notes.Add(bootstrap.Detail);
            return;
        }

        if (provider is ExecutionProviderKind.TensorRTRtx)
        {
            TensorRtRtxBootstrapResult bootstrap = await _tensorRtRtxProviderBootstrap
                .EnsureRegisteredAsync(allowProviderDownloads: true, cancellationToken)
                .ConfigureAwait(false);
            notes.Add(bootstrap.Detail);
            return;
        }

        WindowsMlProviderRegistrationResult result = await WindowsMlProviderRegistrationPolicy.Shared
            .RegisterForReadinessAsync(provider, cancellationToken)
            .ConfigureAwait(false);
        notes.Add(result.Detail);
    }

    private static BenchmarkExecution RunSingleSessionExecution(
        string modelPath,
        BenchmarkProviderPreference preference,
        int runCount,
        ICollection<string> notes)
    {
        EngineCacheProbe.Snapshot cacheBefore = EngineCacheProbe.Capture();
        var coldLoadStopwatch = Stopwatch.StartNew();
        using var sessionLease = CreateSession(modelPath, preference, notes);
        coldLoadStopwatch.Stop();
        ColdLoadAttribution attribution = AttributeColdLoad(
            coldLoadStopwatch.Elapsed.TotalMilliseconds, sessionLease.SelectedProvider, cacheBefore, notes);

        var inputSet = CreateInputs(modelPath, sessionLease.Session.InputMetadata);

        var warmupStopwatch = Stopwatch.StartNew();
        using (sessionLease.Session.Run(inputSet.Values))
        {
        }
        warmupStopwatch.Stop();

        var latencySamples = new List<double>(runCount);
        for (var runIndex = 0; runIndex < runCount; runIndex++)
        {
            var measuredStopwatch = Stopwatch.StartNew();
            using var results = sessionLease.Session.Run(inputSet.Values);
            measuredStopwatch.Stop();

            latencySamples.Add(measuredStopwatch.Elapsed.TotalMilliseconds);
        }

        return new BenchmarkExecution(
            Scenario: "onnx-model",
            RequestedProvider: sessionLease.RequestedProvider,
            SelectedProvider: sessionLease.SelectedProvider,
            ModelSizeBytes: new FileInfo(modelPath).Length,
            ColdLoadMilliseconds: coldLoadStopwatch.Elapsed.TotalMilliseconds,
            WarmupMilliseconds: warmupStopwatch.Elapsed.TotalMilliseconds,
            WarmLatencyAverageMilliseconds: latencySamples.Average(),
            WarmLatencyMinimumMilliseconds: latencySamples.Min(),
            WarmLatencyMaximumMilliseconds: latencySamples.Max(),
            AudioDurationSeconds: inputSet.AudioDurationSeconds,
            EngineCacheOutcome: attribution.Outcome,
            EngineCacheBytesAdded: attribution.BytesAdded,
            EngineCacheFilesAdded: attribution.FilesAdded,
            ColdLoadDominantPhase: attribution.DominantPhase);
    }

    private readonly record struct ColdLoadAttribution(
        string Outcome,
        long BytesAdded,
        int FilesAdded,
        string DominantPhase);

    private static readonly string[] EngineCacheRelevantProviderLabels = ["tensorrt-rtx", "tensorrt", "migraphx"];

    private static ColdLoadAttribution AttributeColdLoad(
        double coldLoadMilliseconds,
        string selectedProvider,
        EngineCacheProbe.Snapshot cacheBefore,
        ICollection<string> notes)
    {
        // Classify from the provider the session actually selected, not the requested
        // preference — a TRT-RTX request that fell back to CPU never touches the engine
        // cache, so scoring it as a GPU cache run can produce a false "hit".
        bool isEngineCacheRelevant = EngineCacheRelevantProviderLabels.Contains(
            selectedProvider, StringComparer.OrdinalIgnoreCase);
        EngineCacheProbe.Snapshot cacheAfter = EngineCacheProbe.Capture();
        string outcome = EngineCacheProbe.ClassifyOutcome(cacheBefore, cacheAfter, isEngineCacheRelevant);
        string dominant = EngineCacheProbe.ClassifyDominantPhase(outcome);
        long bytesAdded = Math.Max(0, cacheAfter.TotalBytes - cacheBefore.TotalBytes);
        int filesAdded = Math.Max(0, cacheAfter.FileCount - cacheBefore.FileCount);
        notes.Add(EngineCacheProbe.FormatNote(coldLoadMilliseconds, outcome, cacheBefore, cacheAfter, dominant));
        return new ColdLoadAttribution(outcome, bytesAdded, filesAdded, dominant);
    }

    private static BenchmarkExecution RunWhisperExecution(
        string encoderModelPath,
        BenchmarkProviderPreference preference,
        int runCount,
        ICollection<string> notes)
    {
        var decoderModelPath = ResolveWhisperDecoderPath(encoderModelPath);
        var configPath = Path.Join(Path.GetDirectoryName(encoderModelPath)!, "..", "config.json");
        var fullConfigPath = Path.GetFullPath(configPath);

        notes.Add($"Whisper decoder discovered at '{decoderModelPath}'.");

        EngineCacheProbe.Snapshot cacheBefore = EngineCacheProbe.Capture();
        var coldLoadStopwatch = Stopwatch.StartNew();
        using var whisperLease = CreateWhisperSessionLease(encoderModelPath, decoderModelPath, preference, notes);
        coldLoadStopwatch.Stop();
        ColdLoadAttribution attribution = AttributeColdLoad(
            coldLoadStopwatch.Elapsed.TotalMilliseconds, whisperLease.SelectedProvider, cacheBefore, notes);

        var encoderInputSet = CreateInputs(encoderModelPath, whisperLease.EncoderSession.InputMetadata);
        var decoderStartTokenId = ResolveWhisperDecoderStartTokenId(fullConfigPath);

        var warmupStopwatch = Stopwatch.StartNew();
        RunWhisperPass(whisperLease, encoderInputSet, decoderStartTokenId);
        warmupStopwatch.Stop();

        var latencySamples = new List<double>(runCount);
        for (var runIndex = 0; runIndex < runCount; runIndex++)
        {
            var measuredStopwatch = Stopwatch.StartNew();
            RunWhisperPass(whisperLease, encoderInputSet, decoderStartTokenId);
            measuredStopwatch.Stop();

            latencySamples.Add(measuredStopwatch.Elapsed.TotalMilliseconds);
        }

        return new BenchmarkExecution(
            Scenario: "whisper-encoder-decoder",
            RequestedProvider: whisperLease.RequestedProvider,
            SelectedProvider: whisperLease.SelectedProvider,
            ModelSizeBytes: new FileInfo(encoderModelPath).Length + new FileInfo(decoderModelPath).Length,
            ColdLoadMilliseconds: coldLoadStopwatch.Elapsed.TotalMilliseconds,
            WarmupMilliseconds: warmupStopwatch.Elapsed.TotalMilliseconds,
            WarmLatencyAverageMilliseconds: latencySamples.Average(),
            WarmLatencyMinimumMilliseconds: latencySamples.Min(),
            WarmLatencyMaximumMilliseconds: latencySamples.Max(),
            AudioDurationSeconds: encoderInputSet.AudioDurationSeconds,
            EngineCacheOutcome: attribution.Outcome,
            EngineCacheBytesAdded: attribution.BytesAdded,
            EngineCacheFilesAdded: attribution.FilesAdded,
            ColdLoadDominantPhase: attribution.DominantPhase);
    }

    private static BenchmarkExecution RunOpusExecution(
        string encoderModelPath,
        BenchmarkProviderPreference preference,
        int runCount,
        ICollection<string> notes)
    {
        var decoderModelPath = ResolveOpusDecoderPath(encoderModelPath);
        var configPath = Path.Join(Path.GetDirectoryName(encoderModelPath)!, "..", "config.json");
        var fullConfigPath = Path.GetFullPath(configPath);

        notes.Add($"Opus decoder discovered at '{decoderModelPath}'.");

        EngineCacheProbe.Snapshot cacheBefore = EngineCacheProbe.Capture();
        var coldLoadStopwatch = Stopwatch.StartNew();
        using var opusLease = CreateOpusSessionLease(encoderModelPath, decoderModelPath, preference, notes);
        coldLoadStopwatch.Stop();
        ColdLoadAttribution attribution = AttributeColdLoad(
            coldLoadStopwatch.Elapsed.TotalMilliseconds, opusLease.SelectedProvider, cacheBefore, notes);

        var encoderInputSet = CreateInputs(encoderModelPath, opusLease.EncoderSession.InputMetadata);
        var decoderStartTokenId = ResolveOpusDecoderStartTokenId(fullConfigPath);

        var warmupStopwatch = Stopwatch.StartNew();
        RunOpusPass(opusLease, encoderInputSet, decoderStartTokenId);
        warmupStopwatch.Stop();

        var latencySamples = new List<double>(runCount);
        for (var runIndex = 0; runIndex < runCount; runIndex++)
        {
            var measuredStopwatch = Stopwatch.StartNew();
            RunOpusPass(opusLease, encoderInputSet, decoderStartTokenId);
            measuredStopwatch.Stop();

            latencySamples.Add(measuredStopwatch.Elapsed.TotalMilliseconds);
        }

        return new BenchmarkExecution(
            Scenario: "opus-mt-encoder-decoder",
            RequestedProvider: opusLease.RequestedProvider,
            SelectedProvider: opusLease.SelectedProvider,
            ModelSizeBytes: new FileInfo(encoderModelPath).Length + new FileInfo(decoderModelPath).Length,
            ColdLoadMilliseconds: coldLoadStopwatch.Elapsed.TotalMilliseconds,
            WarmupMilliseconds: warmupStopwatch.Elapsed.TotalMilliseconds,
            WarmLatencyAverageMilliseconds: latencySamples.Average(),
            WarmLatencyMinimumMilliseconds: latencySamples.Min(),
            WarmLatencyMaximumMilliseconds: latencySamples.Max(),
            AudioDurationSeconds: null,
            EngineCacheOutcome: attribution.Outcome,
            EngineCacheBytesAdded: attribution.BytesAdded,
            EngineCacheFilesAdded: attribution.FilesAdded,
            ColdLoadDominantPhase: attribution.DominantPhase);
    }

    private static bool ShouldUseCatalogDevicePolicyForPreference(BenchmarkProviderPreference preference)
    {
#if WINDOWS
        return OnnxExecutionSessionFactory.ShouldUseCatalogDevicePolicy(
            ActiveWindowsMlDevicePolicy,
            MapPreferenceToExecutionProvider(preference));
#else
        return false;
#endif
    }

    private static string GetCatalogGpuRouteNote(BenchmarkProviderPreference preference, string explicitRouteDescription) =>
        ShouldUseCatalogDevicePolicyForPreference(preference)
            ? "Provider route: WinML catalog device policy (ORT auto-selects among registered catalog EPs)."
            : $"Provider route: {explicitRouteDescription}";

    private static string ResolveBenchmarkEffectiveProviderLabel(
        InferenceSession session,
        BenchmarkProviderPreference preference)
    {
        ExecutionProviderKind optionsSelected = MapPreferenceToExecutionProvider(preference);
        bool useCatalog = ShouldUseCatalogDevicePolicyForPreference(preference);
        ExecutionProviderKind effective = OnnxExecutionSessionFactory.ResolveEffectiveProviderKindFromSession(
            session,
            optionsSelected,
            useCatalog);
        return FormatExecutionProvider(effective);
    }

    private static BenchmarkSessionLease CreateSession(
        string modelPath,
        BenchmarkProviderPreference preference,
        ICollection<string> notes) =>
        preference switch
        {
            BenchmarkProviderPreference.Cpu => CreateCpuSession(modelPath, notes),
            BenchmarkProviderPreference.Dml => CreateDirectMlSession(modelPath, notes),
            BenchmarkProviderPreference.TensorRtRtx => CreateTensorRtRtxSession(modelPath, notes),
            BenchmarkProviderPreference.Migraphx => CreateMigraphxSession(modelPath, notes),
            BenchmarkProviderPreference.Cuda => CreateNativeCudaSession(modelPath, notes),
            BenchmarkProviderPreference.TensorRt => CreateNativeTensorRtSession(modelPath, notes),
            BenchmarkProviderPreference.Qnn => CreateQnnSession(modelPath, notes),
            BenchmarkProviderPreference.Auto => CreateAutoSession(modelPath, notes),
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown provider preference.")
        };

    private static BenchmarkSessionLease CreateCpuSession(string modelPath, ICollection<string> notes)
    {
        notes.Add("Provider route: explicit CPU execution provider.");
        using SessionOptions options = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
        var session = new InferenceSession(modelPath, options);
        return new BenchmarkSessionLease(session, "cpu", "cpu");
    }

    private static BenchmarkSessionLease CreateDirectMlSession(string modelPath, ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Dml;
        notes.Add(GetCatalogGpuRouteNote(preference, "explicit WinML catalog DirectML execution provider."));
        using SessionOptions options = CreateSessionOptions(preference);
        if (!ShouldUseCatalogDevicePolicyForPreference(preference))
        {
            OnnxExecutionSessionFactory.AppendDirectMlProvider(options);
        }

        var session = new InferenceSession(modelPath, options);
        string selectedProviderLabel = ResolveBenchmarkEffectiveProviderLabel(session, preference);
        return new BenchmarkSessionLease(session, "dml", selectedProviderLabel);
    }

    private static BenchmarkSessionLease CreateTensorRtRtxSession(string modelPath, ICollection<string> notes)
    {
        notes.Add(GetCatalogGpuRouteNote(
            BenchmarkProviderPreference.TensorRtRtx,
            "explicit TensorRT RTX EP ABI plugin execution provider."));
        ProviderSession providerSession = CreateTensorRtRtxProviderSession(modelPath, notes);
        return new BenchmarkSessionLease(providerSession.Session, "trt-rtx", providerSession.SelectedProvider);
    }

    private static BenchmarkSessionLease CreateMigraphxSession(string modelPath, ICollection<string> notes)
    {
        notes.Add(GetCatalogGpuRouteNote(
            BenchmarkProviderPreference.Migraphx,
            "explicit MIGraphX execution provider."));
        var session = CreateMigraphxInferenceSession(modelPath, notes);
        string selectedProviderLabel = ResolveBenchmarkEffectiveProviderLabel(session, BenchmarkProviderPreference.Migraphx);
        return new BenchmarkSessionLease(session, "migraphx", selectedProviderLabel);
    }

    private static InferenceSession CreateMigraphxInferenceSession(string modelPath, ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Migraphx;
        using SessionOptions options = CreateSessionOptions(preference);
        ExecutionProviderKind selectedProvider;
        if (ShouldUseCatalogDevicePolicyForPreference(preference))
        {
            selectedProvider = ExecutionProviderKind.Migraphx;
            notes.Add("Selected provider: catalog device policy (ORT auto-selects among registered catalog EPs).");
        }
        else
        {
            selectedProvider = MigraphxSessionOptionsExtensions.AppendMigraphxOrFallback(options);
            string selectedProviderLabel = FormatExecutionProvider(selectedProvider);
            if (selectedProvider is not ExecutionProviderKind.Migraphx)
            {
                throw new InvalidOperationException(
                    "MIGraphX execution provider is not visible to ONNX Runtime; refusing CPU fallback for explicit migraphx benchmark.");
            }

            notes.Add($"Selected provider: {selectedProviderLabel}.");
        }

        return new InferenceSession(modelPath, options);
    }

    private static BenchmarkSessionLease CreateQnnSession(string modelPath, ICollection<string> notes)
    {
        notes.Add(GetCatalogGpuRouteNote(
            BenchmarkProviderPreference.Qnn,
            "explicit WinML catalog QNN execution provider."));
        var session = CreateQnnInferenceSession(modelPath, notes);
        string selectedProviderLabel = ResolveBenchmarkEffectiveProviderLabel(session, BenchmarkProviderPreference.Qnn);
        return new BenchmarkSessionLease(session, "qnn", selectedProviderLabel);
    }

    private static InferenceSession CreateQnnInferenceSession(string modelPath, ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Qnn;
        using SessionOptions options = CreateSessionOptions(preference);
        if (ShouldUseCatalogDevicePolicyForPreference(preference))
        {
            notes.Add("Selected provider: catalog device policy (ORT auto-selects among registered catalog EPs).");
        }
        else
        {
            ExecutionProviderKind selectedProvider = WinMlCatalog.WinMlCatalogSessionOptionsExtensions.AppendQnnOrFallback(options);
            if (selectedProvider is not ExecutionProviderKind.Qnn)
            {
                throw new InvalidOperationException(
                    "QNN execution provider is not visible to ONNX Runtime; refusing CPU fallback for explicit qnn benchmark.");
            }

            notes.Add($"Selected provider: {FormatExecutionProvider(selectedProvider)}.");
        }

        return new InferenceSession(modelPath, options);
    }

    private static BenchmarkSessionLease CreateNativeCudaSession(string modelPath, ICollection<string> notes)
    {
        notes.Add("Provider route: explicit native ORT CUDA execution provider.");
        var session = CreateNativeCudaInferenceSession(modelPath, notes);
        return new BenchmarkSessionLease(session, "cuda", "cuda");
    }

    private static BenchmarkSessionLease CreateNativeTensorRtSession(string modelPath, ICollection<string> notes)
    {
        notes.Add("Provider route: explicit native ORT TensorRT execution provider.");
        var session = CreateNativeTensorRtInferenceSession(modelPath, notes);
        return new BenchmarkSessionLease(session, "tensorrt", "tensorrt");
    }

    private static InferenceSession CreateNativeCudaInferenceSession(string modelPath, ICollection<string> notes)
    {
        using SessionOptions options = CreateSessionOptions(BenchmarkProviderPreference.Cuda);
        ExecutionProviderKind selectedProvider = AppendNativeCudaOrFallback(options);
        if (selectedProvider is not ExecutionProviderKind.Cuda)
        {
            throw new InvalidOperationException(
                $"{NativeCudaTensorRtWindowsProviderConstants.CudaOrtExecutionProviderName} is not available to ONNX Runtime; "
                + "refusing CPU fallback for explicit cuda benchmark.");
        }

        notes.Add("Selected provider: cuda.");
        return new InferenceSession(modelPath, options);
    }

    private static InferenceSession CreateNativeTensorRtInferenceSession(string modelPath, ICollection<string> notes)
    {
        using SessionOptions options = CreateSessionOptions(BenchmarkProviderPreference.TensorRt);
        ExecutionProviderKind selectedProvider = AppendNativeTensorRtOrFallback(options);
        if (selectedProvider is not ExecutionProviderKind.TensorRt)
        {
            throw new InvalidOperationException(
                $"{TensorRtRtxProviderConstants.NativeOrtExecutionProviderName} is not available to ONNX Runtime; "
                + "refusing CPU fallback for explicit tensorrt benchmark.");
        }

        notes.Add("Selected provider: tensorrt.");
        return new InferenceSession(modelPath, options);
    }

    private static ExecutionProviderKind AppendNativeCudaOrFallback(SessionOptions options) =>
        TryAppendNativeCudaProvider(options, out _)
            ? ExecutionProviderKind.Cuda
            : ExecutionProviderKind.Cpu;

    private static ExecutionProviderKind AppendNativeTensorRtOrFallback(SessionOptions options) =>
        TryAppendNativeTensorRtProvider(options, out _)
            ? ExecutionProviderKind.TensorRt
            : ExecutionProviderKind.Cpu;

    private static bool TryAppendNativeCudaProvider(SessionOptions options, out string? failureReason)
    {
        failureReason = null;
        if (!CudaOrtProbe.IsCudaProviderListed())
        {
            failureReason =
                $"{NativeCudaTensorRtWindowsProviderConstants.CudaOrtExecutionProviderName} is not listed by ONNX Runtime.";
            return false;
        }

        try
        {
            options.AppendExecutionProvider_CUDA(deviceId: 0);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private static bool TryAppendNativeTensorRtProvider(SessionOptions options, out string? failureReason)
    {
        failureReason = null;
        if (!NativeTensorRtLibraryProbe.IsNativeTensorRtAvailable())
        {
            failureReason = "Native TensorRT libraries were not found.";
            return false;
        }

        if (!TensorRtRtxOrtProbe.IsNativeTensorRtProviderListed())
        {
            failureReason =
                $"{TensorRtRtxProviderConstants.NativeOrtExecutionProviderName} is not listed by ONNX Runtime.";
            return false;
        }

        try
        {
            options.AppendExecutionProvider_Tensorrt(deviceId: 0);
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    private static BenchmarkSessionLease CreateAutoSession(string modelPath, ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Auto;
        notes.Add("Provider route: auto selected.");

        if (ShouldUseCatalogDevicePolicyForPreference(preference))
        {
            notes.Add("Auto route: catalog device policy (ORT auto-selects among registered catalog EPs).");
            using SessionOptions options = CreateSessionOptions(preference);
            var session = new InferenceSession(modelPath, options);
            string selectedProviderLabel = ResolveBenchmarkEffectiveProviderLabel(session, preference);
            return new BenchmarkSessionLease(session, "auto", selectedProviderLabel);
        }

        try
        {
            using SessionOptions options = CreateSessionOptions(preference);
            OnnxExecutionSessionFactory.AppendDirectMlProvider(options);
            var session = new InferenceSession(modelPath, options);
            notes.Add("Auto route resolved to DirectML.");
            return new BenchmarkSessionLease(session, "auto", "dml");
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            notes.Add($"Auto route fell back to CPU because DirectML was unavailable: {ex.Message}");
            using SessionOptions cpuOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
            var session = new InferenceSession(modelPath, cpuOptions);
            return new BenchmarkSessionLease(session, "auto", "cpu");
        }
    }

    private static SessionOptions CreateSessionOptions(BenchmarkProviderPreference preference)
    {
        SessionOptions options = new()
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        };
#if WINDOWS
        if (OnnxExecutionSessionFactory.ShouldUseCatalogDevicePolicy(
                ActiveWindowsMlDevicePolicy,
                MapPreferenceToExecutionProvider(preference)))
        {
            WindowsMlExecutionDevicePolicyMapper.ApplyIfNeeded(options, ActiveWindowsMlDevicePolicy);
        }
#endif
        return options;
    }

    private static ProviderSession CreateTensorRtRtxProviderSession(
        string modelPath,
        ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.TensorRtRtx;
        using SessionOptions options = CreateSessionOptions(preference);
        if (ShouldUseCatalogDevicePolicyForPreference(preference))
        {
            var session = new InferenceSession(modelPath, options);
            string effectiveProviderLabel = ResolveBenchmarkEffectiveProviderLabel(session, preference);
            notes.Add($"Selected provider: {effectiveProviderLabel} (catalog device policy).");
            return new ProviderSession(session, effectiveProviderLabel);
        }

        ExecutionProviderKind selectedProvider = OnnxExecutionSessionFactory.AppendTensorRtRtxOrFallbackProvider(options);
        string selectedProviderLabel = FormatExecutionProvider(selectedProvider);
        if (selectedProvider is not ExecutionProviderKind.TensorRTRtx)
        {
            throw new InvalidOperationException(
                $"TensorRT RTX EP ABI plugin device is not visible in OrtEnv.GetEpDevices(); refusing fallback to {selectedProviderLabel} for explicit trt-rtx benchmark.");
        }

        return new ProviderSession(new InferenceSession(modelPath, options), selectedProviderLabel);
    }

    private static string FormatProviderPreference(BenchmarkProviderPreference preference) =>
        preference switch
        {
            BenchmarkProviderPreference.Auto => "auto",
            BenchmarkProviderPreference.Cpu => "cpu",
            BenchmarkProviderPreference.Dml => "dml",
            BenchmarkProviderPreference.TensorRtRtx => "trt-rtx",
            BenchmarkProviderPreference.Migraphx => "migraphx",
            BenchmarkProviderPreference.Cuda => "cuda",
            BenchmarkProviderPreference.TensorRt => "tensorrt",
            BenchmarkProviderPreference.Qnn => "qnn",
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown provider preference.")
        };

    private static string FormatExecutionProvider(ExecutionProviderKind provider) =>
        provider switch
        {
            ExecutionProviderKind.Cpu => "cpu",
            ExecutionProviderKind.DirectMl => "dml",
            ExecutionProviderKind.Migraphx => "migraphx",
            ExecutionProviderKind.TensorRTRtx => "trt-rtx",
            ExecutionProviderKind.OpenVino => "openvino",
            ExecutionProviderKind.CoreMl => "coreml",
            ExecutionProviderKind.Cuda => "cuda",
            ExecutionProviderKind.TensorRt => "tensorrt",
            ExecutionProviderKind.Dnnl => "dnnl",
            ExecutionProviderKind.Qnn => "qnn",
            ExecutionProviderKind.OpenVinoCatalog => "openvino-catalog",
            ExecutionProviderKind.VitisAi => "vitisai",
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown execution provider.")
        };

    private static BenchmarkMeasurements EmptyMeasurements() =>
        new(
            ColdLoadMilliseconds: null,
            WarmupMilliseconds: null,
            WarmLatencyAverageMilliseconds: null,
            WarmLatencyMinimumMilliseconds: null,
            WarmLatencyMaximumMilliseconds: null,
            AudioDurationSeconds: null,
            RealTimeFactorAverage: null);

    private static double? CalculateRealTimeFactorAverage(
        double? audioDurationSeconds,
        double averageLatencyMilliseconds)
    {
        if (audioDurationSeconds is null || audioDurationSeconds <= 0)
        {
            return null;
        }

        return (averageLatencyMilliseconds / 1000d) / audioDurationSeconds.Value;
    }

    private static BenchmarkReport CreateReport(
        string scenario,
        BenchmarkStatus status,
        string modelPath,
        string reportPath,
        string requestedProvider,
        string selectedProvider,
        int runCount,
        bool supportsExecution,
        long modelSizeBytes,
        BenchmarkMeasurements measurements,
        string? failureReason,
        IReadOnlyList<string> notes) =>
        new(
            Scenario: scenario,
            ModelPath: modelPath,
            ReportPath: reportPath,
            Status: status,
            RequestedProvider: requestedProvider,
            SelectedProvider: selectedProvider,
            RunCount: runCount,
            SupportsExecution: supportsExecution,
            ModelSizeBytes: modelSizeBytes,
            Measurements: measurements,
            FailureReason: failureReason,
            Notes: notes,
            GeneratedAtUtc: DateTimeOffset.UtcNow);

    private static BenchmarkInputSet CreateInputs(
        string modelPath,
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        var modelProfile = DetermineModelProfile(modelPath, inputMetadata);
        double? audioDurationSeconds = null;

        foreach (var pair in inputMetadata)
        {
            values.Add(CreateInputValue(modelProfile, pair.Key, pair.Value));
        }

        if (modelProfile is BenchmarkModelProfile.SileroVad)
        {
            audioDurationSeconds = 512d / 16000d;
        }
        else if (modelProfile is BenchmarkModelProfile.WhisperEncoder)
        {
            audioDurationSeconds = ResolveWhisperAudioDurationSeconds(inputMetadata);
        }

        return new BenchmarkInputSet(values, audioDurationSeconds);
    }

    private static NamedOnnxValue CreateInputValue(
        BenchmarkModelProfile modelProfile,
        string inputName,
        NodeMetadata metadata)
    {
        if (!metadata.IsTensor)
        {
            throw new NotSupportedException($"Input '{inputName}' is not a tensor input.");
        }

        return metadata.ElementDataType switch
        {
            TensorElementType.Float => NamedOnnxValue.CreateFromTensor(inputName, CreateFloatTensor(modelProfile, inputName, metadata)),
            TensorElementType.Float16 => NamedOnnxValue.CreateFromTensor(inputName, CreateFloat16Tensor(modelProfile, inputName, metadata)),
            TensorElementType.Int32 => NamedOnnxValue.CreateFromTensor(inputName, CreateInt32Tensor(modelProfile, inputName, metadata)),
            TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(inputName, CreateInt64Tensor(modelProfile, inputName, metadata)),
            _ => throw new NotSupportedException($"Input '{inputName}' uses unsupported tensor element type '{metadata.ElementDataType}'.")
        };
    }

    private static DenseTensor<float> CreateFloatTensor(
        BenchmarkModelProfile modelProfile,
        string inputName,
        NodeMetadata metadata)
    {
        var dimensions = ResolveDimensions(modelProfile, inputName, metadata);
        var count = CountElements(dimensions);
        var data = new float[count];

        if (modelProfile is BenchmarkModelProfile.SileroVad && inputName.Equals("input", StringComparison.Ordinal))
        {
            if (data.Length > 0)
            {
                data[0] = 0.1f;
            }
        }
        else if (modelProfile is BenchmarkModelProfile.WhisperEncoder && IsWhisperAudioFeaturesInput(inputName))
        {
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = (index % 11) * 0.01f;
            }
        }

        return new DenseTensor<float>(data, dimensions);
    }

    private static DenseTensor<Float16> CreateFloat16Tensor(
        BenchmarkModelProfile modelProfile,
        string inputName,
        NodeMetadata metadata)
    {
        var dimensions = ResolveDimensions(modelProfile, inputName, metadata);
        var count = CountElements(dimensions);
        var data = new Float16[count];

        if (modelProfile is BenchmarkModelProfile.WhisperEncoder && IsWhisperAudioFeaturesInput(inputName))
        {
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = (Float16)((index % 11) * 0.01f);
            }
        }

        return new DenseTensor<Float16>(data, dimensions);
    }

    private static DenseTensor<long> CreateInt64Tensor(
        BenchmarkModelProfile modelProfile,
        string inputName,
        NodeMetadata metadata)
    {
        var dimensions = ResolveDimensions(modelProfile, inputName, metadata);
        var count = CountElements(dimensions);
        var data = new long[count];

        if (modelProfile is BenchmarkModelProfile.SileroVad && inputName.Equals("sr", StringComparison.Ordinal) && data.Length > 0)
        {
            data[0] = 16000L;
        }
        else if (modelProfile is BenchmarkModelProfile.OpusEncoder)
        {
            FillOpusEncoderTensor(inputName, data);
        }

        return new DenseTensor<long>(data, dimensions);
    }

    private static DenseTensor<int> CreateInt32Tensor(
        BenchmarkModelProfile modelProfile,
        string inputName,
        NodeMetadata metadata)
    {
        var dimensions = ResolveDimensions(modelProfile, inputName, metadata);
        var count = CountElements(dimensions);
        var data = new int[count];

        if (modelProfile is BenchmarkModelProfile.OpusEncoder)
        {
            var longData = new long[count];
            FillOpusEncoderTensor(inputName, longData);
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = checked((int)longData[index]);
            }
        }

        return new DenseTensor<int>(data, dimensions);
    }

    private static int[] ResolveDimensions(BenchmarkModelProfile modelProfile, string inputName, NodeMetadata metadata)
    {
        if (modelProfile is BenchmarkModelProfile.SileroVad)
        {
            if (inputName.Equals("input", StringComparison.Ordinal))
            {
                return [1, 512];
            }

            if (inputName.Equals("state", StringComparison.Ordinal))
            {
                return [2, 1, 128];
            }

            if (inputName.Equals("sr", StringComparison.Ordinal))
            {
                return [1];
            }
        }
        else if (modelProfile is BenchmarkModelProfile.WhisperEncoder && IsWhisperAudioFeaturesInput(inputName))
        {
            return [1, 80, 3000];
        }
        else if (modelProfile is BenchmarkModelProfile.OpusEncoder)
        {
            return inputName switch
            {
                "input_ids" => [1, 8],
                "attention_mask" => [1, 8],
                _ => metadata.Dimensions.Select(ToPositiveDimension).ToArray()
            };
        }

        return metadata.Dimensions.Select(ToPositiveDimension).ToArray();
    }

    private static BenchmarkModelProfile DetermineModelProfile(
        string modelPath,
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        if (LooksLikeSileroVad(modelPath, inputMetadata))
        {
            return BenchmarkModelProfile.SileroVad;
        }

        if (LooksLikeWhisperEncoder(modelPath))
        {
            return BenchmarkModelProfile.WhisperEncoder;
        }

        if (LooksLikeOpusEncoder(modelPath))
        {
            return BenchmarkModelProfile.OpusEncoder;
        }

        return BenchmarkModelProfile.Generic;
    }

    private static bool LooksLikeSileroVad(
        string modelPath,
        IReadOnlyDictionary<string, NodeMetadata>? inputMetadata)
    {
        var normalizedPath = modelPath.Replace('/', '\\');
        if (normalizedPath.Contains("silero", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (inputMetadata is null)
        {
            return false;
        }

        return inputMetadata.ContainsKey("input")
            && inputMetadata.ContainsKey("state")
            && inputMetadata.ContainsKey("sr");
    }

    private static string ResolveModelPath(string suppliedPath, ICollection<string> notes)
    {
        BenchmarkModelCandidate resolution = BenchmarkModelPathResolver.CreateDefault().ResolveSingle(suppliedPath);
        notes.Add(resolution.ResolutionNote);
        return resolution.ModelPath;
    }

    private static bool LooksLikeWhisperEncoder(string modelPath)
    {
        var fileName = Path.GetFileName(modelPath);
        var parentDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        return (fileName.StartsWith("encoder_model", StringComparison.OrdinalIgnoreCase)
                || fileName.Equals("encoder.onnx", StringComparison.OrdinalIgnoreCase))
            && parentDirectory.Contains("whisper", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWhisperAudioFeaturesInput(string inputName)
    {
        return inputName.Equals("input_features", StringComparison.Ordinal)
            || inputName.Equals("audio_features", StringComparison.Ordinal);
    }

    private static bool LooksLikeOpusEncoder(string modelPath)
    {
        var fileName = Path.GetFileName(modelPath);
        var parentDirectory = Path.GetDirectoryName(modelPath) ?? string.Empty;
        return fileName.StartsWith("encoder_model", StringComparison.OrdinalIgnoreCase)
            && parentDirectory.Contains("opus", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveWhisperDecoderPath(string encoderModelPath)
    {
        var modelDirectory = Path.GetDirectoryName(encoderModelPath)!;
        foreach (var fileName in new[] { "decoder_model.onnx", "decoder.onnx" })
        {
            var decoderModelPath = Path.Join(modelDirectory, fileName);
            if (File.Exists(decoderModelPath))
            {
                return Path.GetFullPath(decoderModelPath);
            }
        }

        throw new FileNotFoundException(
            "Whisper decoder model was not found next to the encoder model.",
            Path.Join(modelDirectory, "decoder_model.onnx"));
    }

    private static int ResolveWhisperDecoderStartTokenId(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return 50258;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("decoder_start_token_id", out var tokenIdElement) &&
            tokenIdElement.TryGetInt32(out var tokenId))
        {
            return tokenId;
        }

        return 50258;
    }

    private static string ResolveOpusDecoderPath(string encoderModelPath)
    {
        var decoderModelPath = Path.Join(Path.GetDirectoryName(encoderModelPath)!, "decoder_model.onnx");
        if (File.Exists(decoderModelPath))
        {
            return Path.GetFullPath(decoderModelPath);
        }

        var mergedDecoderModelPath = Path.Join(Path.GetDirectoryName(encoderModelPath)!, "decoder_model_merged.onnx");
        if (!File.Exists(mergedDecoderModelPath))
        {
            throw new FileNotFoundException("Opus decoder model was not found next to the encoder model.", decoderModelPath);
        }

        return Path.GetFullPath(mergedDecoderModelPath);
    }

    private static int ResolveOpusDecoderStartTokenId(string configPath)
    {
        if (!File.Exists(configPath))
        {
            return 65000;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(configPath));
        if (document.RootElement.TryGetProperty("decoder_start_token_id", out var tokenIdElement) &&
            tokenIdElement.TryGetInt32(out var tokenId))
        {
            return tokenId;
        }

        return 65000;
    }

    private static double? ResolveWhisperAudioDurationSeconds(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata)
    {
        if (!inputMetadata.TryGetValue("input_features", out var metadata)
            && !inputMetadata.TryGetValue("audio_features", out metadata))
        {
            return null;
        }

        var dimensions = metadata.Dimensions.Select(ToPositiveDimension).ToArray();
        if (dimensions.Length < 3)
        {
            return null;
        }

        return dimensions[2] / 100d;
    }

    private static void RunWhisperPass(
        WhisperSessionLease lease,
        BenchmarkInputSet encoderInputSet,
        int decoderStartTokenId)
    {
        using var encoderResults = lease.EncoderSession.Run(encoderInputSet.Values);
        using var decoderInputs = CreateWhisperDecoderInputs(
            lease.DecoderSession.InputMetadata,
            encoderResults,
            decoderStartTokenId);
        using var decoderResults = lease.DecoderSession.Run(decoderInputs.Values);
    }

    private static void RunOpusPass(
        OpusSessionLease lease,
        BenchmarkInputSet encoderInputSet,
        int decoderStartTokenId)
    {
        using var encoderResults = lease.EncoderSession.Run(encoderInputSet.Values);
        var encoderHiddenStates = encoderResults.First().AsTensor<float>();
        using var decoderInputs = CreateOpusDecoderInputs(
            lease.DecoderSession.InputMetadata,
            encoderHiddenStates,
            decoderStartTokenId,
            encoderInputSet.Values);
        using var decoderResults = lease.DecoderSession.Run(decoderInputs.Values);
    }

    private static DecoderInputSet CreateWhisperDecoderInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        IEnumerable<DisposableNamedOnnxValue> encoderResults,
        int decoderStartTokenId)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        var encoderOutputByName = encoderResults.ToDictionary(static value => value.Name, StringComparer.Ordinal);

        foreach (var pair in inputMetadata)
        {
            values.Add(pair.Key switch
            {
                "input_ids" => CreateWhisperDecoderInputIds(pair.Key, pair.Value, decoderStartTokenId),
                "encoder_hidden_states" => CreateWhisperDecoderTensorFromEncoderOutput(
                    pair.Key,
                    pair.Value,
                    encoderOutputByName),
                _ when TryResolveWhisperEncoderOutputName(pair.Key, out _) => CreateWhisperDecoderTensorFromEncoderOutput(
                    pair.Key,
                    pair.Value,
                    encoderOutputByName),
                _ when IsWhisperSelfPastInput(pair.Key) => CreateWhisperSelfPastInput(pair.Key, pair.Value),
                _ => throw new NotSupportedException($"Whisper decoder input '{pair.Key}' is not supported by the benchmark harness yet.")
            });
        }

        return new DecoderInputSet(values);
    }

    private static NamedOnnxValue CreateWhisperDecoderInputIds(
        string inputName,
        NodeMetadata metadata,
        int decoderStartTokenId)
    {
        if (!metadata.IsTensor)
        {
            throw new NotSupportedException($"Whisper decoder input '{inputName}' is not a tensor input.");
        }

        return metadata.ElementDataType switch
        {
            TensorElementType.Int32 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<int>(new[] { decoderStartTokenId }, [1, 1])),
            TensorElementType.Int64 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<long>(new[] { (long)decoderStartTokenId }, [1, 1])),
            _ => throw new NotSupportedException(
                $"Whisper decoder input '{inputName}' uses unsupported token tensor element type '{metadata.ElementDataType}'.")
        };
    }

    private static NamedOnnxValue CreateWhisperDecoderTensorFromEncoderOutput(
        string inputName,
        NodeMetadata metadata,
        IReadOnlyDictionary<string, DisposableNamedOnnxValue> encoderOutputByName)
    {
        if (!metadata.IsTensor)
        {
            throw new NotSupportedException($"Whisper decoder input '{inputName}' is not a tensor input.");
        }

        if (!TryResolveWhisperEncoderOutputName(inputName, out var outputName)
            || !encoderOutputByName.TryGetValue(outputName, out var encoderOutput))
        {
            throw new NotSupportedException(
                $"Whisper decoder input '{inputName}' could not be matched to an encoder output.");
        }

        return metadata.ElementDataType switch
        {
            TensorElementType.Float => NamedOnnxValue.CreateFromTensor(inputName, encoderOutput.AsTensor<float>()),
            TensorElementType.Float16 => NamedOnnxValue.CreateFromTensor(inputName, encoderOutput.AsTensor<Float16>()),
            _ => throw new NotSupportedException(
                $"Whisper decoder input '{inputName}' uses unsupported encoder-output tensor element type '{metadata.ElementDataType}'.")
        };
    }

    private static NamedOnnxValue CreateWhisperSelfPastInput(string inputName, NodeMetadata metadata)
    {
        if (!metadata.IsTensor)
        {
            throw new NotSupportedException($"Whisper decoder input '{inputName}' is not a tensor input.");
        }

        var dimensions = ResolveWhisperSelfPastDimensions(metadata);
        return metadata.ElementDataType switch
        {
            TensorElementType.Float => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<float>(new float[CountElements(dimensions)], dimensions)),
            TensorElementType.Float16 => NamedOnnxValue.CreateFromTensor(
                inputName,
                new DenseTensor<Float16>(new Float16[CountElements(dimensions)], dimensions)),
            _ => throw new NotSupportedException(
                $"Whisper decoder input '{inputName}' uses unsupported self-cache tensor element type '{metadata.ElementDataType}'.")
        };
    }

    private static bool TryResolveWhisperEncoderOutputName(string decoderInputName, out string encoderOutputName)
    {
        if (decoderInputName.Equals("encoder_hidden_states", StringComparison.Ordinal))
        {
            encoderOutputName = "hidden_states";
            return true;
        }

        if (decoderInputName.StartsWith("past_key_cross_", StringComparison.Ordinal))
        {
            encoderOutputName = "present_key_cross_" + decoderInputName["past_key_cross_".Length..];
            return true;
        }

        if (decoderInputName.StartsWith("past_value_cross_", StringComparison.Ordinal))
        {
            encoderOutputName = "present_value_cross_" + decoderInputName["past_value_cross_".Length..];
            return true;
        }

        encoderOutputName = string.Empty;
        return false;
    }

    private static bool IsWhisperSelfPastInput(string inputName)
    {
        return inputName.StartsWith("past_key_self_", StringComparison.Ordinal)
            || inputName.StartsWith("past_value_self_", StringComparison.Ordinal);
    }

    private static int[] ResolveWhisperSelfPastDimensions(NodeMetadata metadata)
    {
        var dimensions = metadata.Dimensions.Select(ToPositiveDimension).ToArray();
        if (dimensions.Length >= 3)
        {
            dimensions[2] = 0;
        }

        return dimensions;
    }

    private static DecoderInputSet CreateOpusDecoderInputs(
        IReadOnlyDictionary<string, NodeMetadata> inputMetadata,
        Tensor<float> encoderHiddenStates,
        int decoderStartTokenId,
        IReadOnlyList<NamedOnnxValue> encoderInputs)
    {
        var values = new List<NamedOnnxValue>(inputMetadata.Count);
        var encoderAttentionMask = encoderInputs
            .FirstOrDefault(static value => value.Name.Equals("attention_mask", StringComparison.Ordinal))
            ?.AsTensor<long>();

        foreach (var pair in inputMetadata)
        {
            values.Add(pair.Key switch
            {
                "input_ids" => NamedOnnxValue.CreateFromTensor(
                    "input_ids",
                    new DenseTensor<long>(new long[] { decoderStartTokenId }, [1, 1])),
                "encoder_hidden_states" => NamedOnnxValue.CreateFromTensor("encoder_hidden_states", encoderHiddenStates),
                "attention_mask" when encoderAttentionMask is not null => NamedOnnxValue.CreateFromTensor("attention_mask", encoderAttentionMask),
                "encoder_attention_mask" when encoderAttentionMask is not null => NamedOnnxValue.CreateFromTensor("encoder_attention_mask", encoderAttentionMask),
                _ => throw new NotSupportedException($"Opus decoder input '{pair.Key}' is not supported by the benchmark harness yet.")
            });
        }

        return new DecoderInputSet(values);
    }

    private static WhisperSessionLease CreateWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        BenchmarkProviderPreference preference,
        ICollection<string> notes) =>
        preference switch
        {
            BenchmarkProviderPreference.Cpu => CreateCpuWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Dml => CreateDirectMlWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.TensorRtRtx => CreateTensorRtRtxWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Migraphx => CreateMigraphxWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Cuda => CreateNativeCudaWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.TensorRt => CreateNativeTensorRtWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Qnn => CreateQnnWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Auto => CreateAutoWhisperSessionLease(encoderModelPath, decoderModelPath, notes),
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown provider preference.")
        };

    private static OpusSessionLease CreateOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        BenchmarkProviderPreference preference,
        ICollection<string> notes) =>
        preference switch
        {
            BenchmarkProviderPreference.Cpu => CreateCpuOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Dml => CreateDirectMlOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.TensorRtRtx => CreateTensorRtRtxOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Migraphx => CreateMigraphxOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Cuda => CreateNativeCudaOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.TensorRt => CreateNativeTensorRtOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Qnn => CreateQnnOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            BenchmarkProviderPreference.Auto => CreateAutoOpusSessionLease(encoderModelPath, decoderModelPath, notes),
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, "Unknown provider preference.")
        };

    private static WhisperSessionLease CreateCpuWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit CPU execution provider.");
        using SessionOptions encoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
        using SessionOptions decoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
        return new WhisperSessionLease(
            new InferenceSession(encoderModelPath, encoderOptions),
            new InferenceSession(decoderModelPath, decoderOptions),
            "cpu",
            "cpu");
    }

    private static WhisperSessionLease CreateDirectMlWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Dml;
        notes.Add(GetCatalogGpuRouteNote(preference, "explicit WinML catalog DirectML execution provider."));
        var encoderSession = CreateDirectMlSession(encoderModelPath);
        var decoderSession = CreateDirectMlSession(decoderModelPath);
        string selectedProviderLabel = ResolvePairSelectedProvider(
            ResolveBenchmarkEffectiveProviderLabel(encoderSession, preference),
            ResolveBenchmarkEffectiveProviderLabel(decoderSession, preference),
            notes);
        return new WhisperSessionLease(encoderSession, decoderSession, "dml", selectedProviderLabel);
    }

    private static WhisperSessionLease CreateTensorRtRtxWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit TensorRT RTX EP ABI plugin execution provider.");
        ProviderSession encoderSession = CreateTensorRtRtxProviderSession(encoderModelPath, notes);
        ProviderSession decoderSession = CreateTensorRtRtxProviderSession(decoderModelPath, notes);
        return new WhisperSessionLease(
            encoderSession.Session,
            decoderSession.Session,
            "trt-rtx",
            ResolvePairSelectedProvider(encoderSession.SelectedProvider, decoderSession.SelectedProvider, notes));
    }

    private static WhisperSessionLease CreateNativeCudaWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit native ORT CUDA execution provider.");
        return new WhisperSessionLease(
            CreateNativeCudaInferenceSession(encoderModelPath, notes),
            CreateNativeCudaInferenceSession(decoderModelPath, notes),
            "cuda",
            "cuda");
    }

    private static WhisperSessionLease CreateNativeTensorRtWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit native ORT TensorRT execution provider.");
        return new WhisperSessionLease(
            CreateNativeTensorRtInferenceSession(encoderModelPath, notes),
            CreateNativeTensorRtInferenceSession(decoderModelPath, notes),
            "tensorrt",
            "tensorrt");
    }

    private static WhisperSessionLease CreateMigraphxWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit MIGraphX execution provider.");
        return new WhisperSessionLease(
            CreateMigraphxInferenceSession(encoderModelPath, notes),
            CreateMigraphxInferenceSession(decoderModelPath, notes),
            "migraphx",
            "migraphx");
    }

    private static WhisperSessionLease CreateQnnWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit WinML catalog QNN execution provider.");
        return new WhisperSessionLease(
            CreateQnnInferenceSession(encoderModelPath, notes),
            CreateQnnInferenceSession(decoderModelPath, notes),
            "qnn",
            "qnn");
    }

    private static WhisperSessionLease CreateAutoWhisperSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: auto selected.");

        try
        {
            var encoderSession = CreateDirectMlSession(encoderModelPath);
            var decoderSession = CreateDirectMlSession(decoderModelPath);
            notes.Add("Auto route resolved to DirectML.");
            return new WhisperSessionLease(encoderSession, decoderSession, "auto", "dml");
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            notes.Add($"Auto route fell back to CPU because DirectML was unavailable: {ex.Message}");
            using SessionOptions encoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
            using SessionOptions decoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
            return new WhisperSessionLease(
                new InferenceSession(encoderModelPath, encoderOptions),
                new InferenceSession(decoderModelPath, decoderOptions),
                "auto",
                "cpu");
        }
    }

    private static OpusSessionLease CreateCpuOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit CPU execution provider.");
        using SessionOptions encoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
        using SessionOptions decoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
        return new OpusSessionLease(
            new InferenceSession(encoderModelPath, encoderOptions),
            new InferenceSession(decoderModelPath, decoderOptions),
            "cpu",
            "cpu");
    }

    private static OpusSessionLease CreateDirectMlOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Dml;
        notes.Add(GetCatalogGpuRouteNote(preference, "explicit WinML catalog DirectML execution provider."));
        var encoderSession = CreateDirectMlSession(encoderModelPath);
        var decoderSession = CreateDirectMlSession(decoderModelPath);
        string selectedProviderLabel = ResolvePairSelectedProvider(
            ResolveBenchmarkEffectiveProviderLabel(encoderSession, preference),
            ResolveBenchmarkEffectiveProviderLabel(decoderSession, preference),
            notes);
        return new OpusSessionLease(encoderSession, decoderSession, "dml", selectedProviderLabel);
    }

    private static OpusSessionLease CreateTensorRtRtxOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit TensorRT RTX EP ABI plugin execution provider.");
        ProviderSession encoderSession = CreateTensorRtRtxProviderSession(encoderModelPath, notes);
        ProviderSession decoderSession = CreateTensorRtRtxProviderSession(decoderModelPath, notes);
        return new OpusSessionLease(
            encoderSession.Session,
            decoderSession.Session,
            "trt-rtx",
            ResolvePairSelectedProvider(encoderSession.SelectedProvider, decoderSession.SelectedProvider, notes));
    }

    private static OpusSessionLease CreateMigraphxOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit MIGraphX execution provider.");
        return new OpusSessionLease(
            CreateMigraphxInferenceSession(encoderModelPath, notes),
            CreateMigraphxInferenceSession(decoderModelPath, notes),
            "migraphx",
            "migraphx");
    }

    private static OpusSessionLease CreateQnnOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit WinML catalog QNN execution provider.");
        return new OpusSessionLease(
            CreateQnnInferenceSession(encoderModelPath, notes),
            CreateQnnInferenceSession(decoderModelPath, notes),
            "qnn",
            "qnn");
    }

    private static OpusSessionLease CreateNativeCudaOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit native ORT CUDA execution provider.");
        return new OpusSessionLease(
            CreateNativeCudaInferenceSession(encoderModelPath, notes),
            CreateNativeCudaInferenceSession(decoderModelPath, notes),
            "cuda",
            "cuda");
    }

    private static OpusSessionLease CreateNativeTensorRtOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: explicit native ORT TensorRT execution provider.");
        return new OpusSessionLease(
            CreateNativeTensorRtInferenceSession(encoderModelPath, notes),
            CreateNativeTensorRtInferenceSession(decoderModelPath, notes),
            "tensorrt",
            "tensorrt");
    }

    private static OpusSessionLease CreateAutoOpusSessionLease(
        string encoderModelPath,
        string decoderModelPath,
        ICollection<string> notes)
    {
        notes.Add("Provider route: auto selected.");

        try
        {
            var encoderSession = CreateDirectMlSession(encoderModelPath);
            var decoderSession = CreateDirectMlSession(decoderModelPath);
            notes.Add("Auto route resolved to DirectML.");
            return new OpusSessionLease(encoderSession, decoderSession, "auto", "dml");
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or EntryPointNotFoundException or DllNotFoundException)
        {
            notes.Add($"Auto route fell back to CPU because DirectML was unavailable: {ex.Message}");
            using SessionOptions encoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
            using SessionOptions decoderOptions = CreateSessionOptions(BenchmarkProviderPreference.Cpu);
            return new OpusSessionLease(
                new InferenceSession(encoderModelPath, encoderOptions),
                new InferenceSession(decoderModelPath, decoderOptions),
                "auto",
                "cpu");
        }
    }

    private static InferenceSession CreateDirectMlSession(string modelPath)
    {
        const BenchmarkProviderPreference preference = BenchmarkProviderPreference.Dml;
        using SessionOptions options = CreateSessionOptions(preference);
        if (!ShouldUseCatalogDevicePolicyForPreference(preference))
        {
            OnnxExecutionSessionFactory.AppendDirectMlProvider(options);
        }

        return new InferenceSession(modelPath, options);
    }

    private static string ResolvePairSelectedProvider(
        string firstSelectedProvider,
        string secondSelectedProvider,
        ICollection<string> notes)
    {
        if (firstSelectedProvider.Equals(secondSelectedProvider, StringComparison.Ordinal))
        {
            return firstSelectedProvider;
        }

        notes.Add($"Paired model sessions resolved to mixed providers ({firstSelectedProvider}, {secondSelectedProvider}); reporting cpu.");
        return "cpu";
    }

    private static void FillOpusEncoderTensor(string inputName, long[] data)
    {
        if (data.Length == 0)
        {
            return;
        }

        if (inputName.Equals("attention_mask", StringComparison.Ordinal))
        {
            Array.Fill(data, 1L);
            return;
        }

        if (inputName.Equals("input_ids", StringComparison.Ordinal))
        {
            long[] sampleTokens = [250, 142, 77, 901, 54, 12, 0, 65000];
            for (var index = 0; index < data.Length; index++)
            {
                data[index] = sampleTokens[index % sampleTokens.Length];
            }
        }
    }

    private static string ResolveFailureScenario(string modelPath)
    {
        if (LooksLikeWhisperEncoder(modelPath))
        {
            return "whisper-encoder-decoder";
        }

        if (LooksLikeOpusEncoder(modelPath))
        {
            return "opus-mt-encoder-decoder";
        }

        return "onnx-model";
    }

    private static int ToPositiveDimension(int dimension) => dimension <= 0 ? 1 : dimension;

    private static int CountElements(IEnumerable<int> dimensions)
    {
        var count = 1;
        foreach (var dimension in dimensions)
        {
            checked
            {
                count *= dimension;
            }
        }

        return count;
    }

    private sealed record BenchmarkInputSet(
        IReadOnlyList<NamedOnnxValue> Values,
        double? AudioDurationSeconds);

    private sealed class DecoderInputSet : IDisposable
    {
        public DecoderInputSet(IReadOnlyList<NamedOnnxValue> values)
        {
            Values = values;
        }

        public IReadOnlyList<NamedOnnxValue> Values { get; }

        public void Dispose()
        {
            foreach (var value in Values.OfType<IDisposable>())
            {
                value.Dispose();
            }
        }
    }

    private sealed record BenchmarkSessionLease(
        InferenceSession Session,
        string RequestedProvider,
        string SelectedProvider) : IDisposable
    {
        public void Dispose() => Session.Dispose();
    }

    private sealed record ProviderSession(
        InferenceSession Session,
        string SelectedProvider);

    private sealed record WhisperSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderSession,
        string RequestedProvider,
        string SelectedProvider) : IDisposable
    {
        public void Dispose()
        {
            DecoderSession.Dispose();
            EncoderSession.Dispose();
        }
    }

    private sealed record OpusSessionLease(
        InferenceSession EncoderSession,
        InferenceSession DecoderSession,
        string RequestedProvider,
        string SelectedProvider) : IDisposable
    {
        public void Dispose()
        {
            DecoderSession.Dispose();
            EncoderSession.Dispose();
        }
    }

    private sealed record BenchmarkExecution(
        string Scenario,
        string RequestedProvider,
        string SelectedProvider,
        long ModelSizeBytes,
        double ColdLoadMilliseconds,
        double WarmupMilliseconds,
        double WarmLatencyAverageMilliseconds,
        double WarmLatencyMinimumMilliseconds,
        double WarmLatencyMaximumMilliseconds,
        double? AudioDurationSeconds,
        string? EngineCacheOutcome = null,
        long? EngineCacheBytesAdded = null,
        int? EngineCacheFilesAdded = null,
        string? ColdLoadDominantPhase = null);

    private enum BenchmarkModelProfile
    {
        Generic,
        SileroVad,
        WhisperEncoder,
        OpusEncoder
    }
}
