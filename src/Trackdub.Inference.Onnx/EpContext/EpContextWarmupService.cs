using System.Diagnostics;
using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Madlad;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;
#if WINDOWS
using Trackdub.Inference.Onnx.WindowsMl;
#endif
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// One-shot warm pass: compile EP-context models for local ONNX graphs, then load each once so
/// <c>nv_runtime_cache_path</c> receives residual CUDA kernels. Re-run after install/update/driver change.
/// </summary>
public sealed class EpContextWarmupService : IEpContextWarmupService
{
    private static readonly string[] SkippedNameFragments =
    [
        // GenAI loads can hard-crash the host under TRT; never AOT those graphs.
        "phi-",
        "Phi-",
    ];

    private readonly IAppStoragePaths _storagePaths;
    private readonly IHardwareProfileProvider _hardwareProfileProvider;
    private readonly EpContextCompiler _compiler;

    public EpContextWarmupService(
        IAppStoragePaths storagePaths,
        IHardwareProfileProvider? hardwareProfileProvider = null,
        EpContextCompiler? compiler = null)
    {
        _storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        _hardwareProfileProvider = hardwareProfileProvider ?? new MachineHardwareProfileProvider();
        _compiler = compiler ?? new EpContextCompiler();
    }

    public async Task<EpContextWarmReport> WarmAsync(
        IReadOnlyList<string>? sourceModelPaths = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        HardwareProfile hardware = await _hardwareProfileProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        string fingerprint = EpContextLoadPathResolver.CurrentEnvironmentFingerprint;

        IReadOnlyList<string> sources = sourceModelPaths is { Count: > 0 }
            ? sourceModelPaths
            : DiscoverLocalOnnxModels(_storagePaths.ModelCacheDirectory);

        var items = new List<EpContextWarmItem>(sources.Count);
        int compiled = 0, reused = 0, skipped = 0, failed = 0;

        foreach (string sourcePath in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (EpContextWarmItem item, bool didCompile, bool didReuse) = await WarmSourceAsync(
                sourcePath, hardware, progress, cancellationToken).ConfigureAwait(false);
            items.Add(item);
            if (didCompile && item.Status != "failed") compiled++;
            if (didReuse && item.Status != "failed") reused++;
            if (item.Status == "skipped") skipped++;
            if (item.Status == "failed") failed++;
        }

        return new EpContextWarmReport(
            _storagePaths.EngineCacheDirectory,
            fingerprint,
            compiled,
            reused,
            skipped,
            failed,
            items);
    }

    private async Task<(EpContextWarmItem Item, bool Compiled, bool Reused)> WarmSourceAsync(
        string sourcePath,
        HardwareProfile hardware,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (ShouldSkip(sourcePath))
        {
            return (new EpContextWarmItem(sourcePath, "skipped", null, 0, 0,
                "Excluded family or already an EP-context artifact."), false, false);
        }

        if (MadladTrtProfiles.GetAotExclusionReason(sourcePath) is { } exclusionReason)
        {
            return (new EpContextWarmItem(sourcePath, "skipped", null, 0, 0, exclusionReason), false, false);
        }

        string epContextPath = EpContextArtifact.GetEpContextPath(sourcePath);
        bool haveValid = EpContextLoadPathResolver.TryResolveLoadPath(
            sourcePath,
            OnnxExecutionSessionFactory.BuildTensorRtRtxOptions(EpContextTrtProfiles.Resolve(sourcePath))) is not null;
        double compileMs = 0;
        if (!haveValid)
        {
            progress?.Report($"compile {Path.GetFileName(sourcePath)}");
            EpContextCompiler.CompileResult compile = await _compiler
                .CompileAsync(sourcePath, epContextPath, cancellationToken).ConfigureAwait(false);
            compileMs = compile.CompileMilliseconds;
            if (!compile.Success)
            {
                return (new EpContextWarmItem(sourcePath, "failed", null, compileMs, 0, compile.FailureReason), false, false);
            }

            // Compilation publishes the artifact and stamp under one cross-process lock.
        }

        // Residual JIT: load once so nv_runtime_cache_path stores CUDA kernels for this GPU.
        progress?.Report($"warm {Path.GetFileName(sourcePath)}");
        double warmMs = TryWarmLoad(
            epContextPath,
            EpContextTrtProfiles.Resolve(sourcePath),
            cancellationToken,
            out string? warmFailure);
        return (new EpContextWarmItem(
            sourcePath, warmFailure is null ? (haveValid ? "reused" : "compiled") : "failed",
            epContextPath, compileMs, warmMs,
            warmFailure ?? (haveValid ? "Existing EP-context artifact is current." : null)),
            !haveValid, haveValid);
    }

    private static bool ShouldSkip(string sourcePath)
    {
        if (EpContextArtifact.IsEpContextPath(sourcePath))
        {
            return true;
        }

        string fileName = Path.GetFileName(sourcePath);
        return SkippedNameFragments.Any(fragment => fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> DiscoverLocalOnnxModels(string modelCacheDirectory)
    {
        if (!Directory.Exists(modelCacheDirectory))
        {
            return [];
        }

        return Directory
            .EnumerateFiles(modelCacheDirectory, "*.onnx", SearchOption.AllDirectories)
            .Where(path => !EpContextArtifact.IsEpContextPath(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private double TryWarmLoad(
        string modelPath,
        IReadOnlyDictionary<string, string>? modelTrtOptions,
        CancellationToken cancellationToken,
        out string? failureReason)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
#if WINDOWS
            WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif
            using SessionOptions options = new();
            ExecutionProviderKind selectedProvider =
                OnnxExecutionSessionFactory.AppendTensorRtRtxOrFallbackProvider(options, modelTrtOptions);
            options.LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING;
            InferenceSession session = new(modelPath, options);
            if (selectedProvider is ExecutionProviderKind.TensorRTRtx)
            {
                TensorRtRtxTeardownGuard.Track(session);
            }

            try
            {
                stopwatch.Stop();
                failureReason = null;
                return stopwatch.Elapsed.TotalMilliseconds;
            }
            finally
            {
                TensorRtRtxTeardownGuard.DisposeSafely(session);
            }
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            stopwatch.Stop();
            TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(ex);
            failureReason = ex.Message;
            return stopwatch.Elapsed.TotalMilliseconds;
        }
    }
}
