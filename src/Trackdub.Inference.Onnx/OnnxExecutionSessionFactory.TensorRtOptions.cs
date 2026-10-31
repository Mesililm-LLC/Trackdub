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

        if (additionalTrtOptions is not null)
        {
            MergeAdditionalTensorRtRtxOptions(trtOptions, additionalTrtOptions);
        }

        TensorRtRtxDriverWorkarounds.Apply(
            trtOptions,
            EpContextLoadPathResolver.CurrentHardware.DriverVersion,
            OperatingSystem.IsWindows());
        return trtOptions;
    }

    private static void MergeAdditionalTensorRtRtxOptions(
        Dictionary<string, string> trtOptions,
        IReadOnlyDictionary<string, string> additionalTrtOptions)
    {
        foreach ((string key, string value) in additionalTrtOptions)
        {
            // EP ABI 0.5.0 removed device_id (the device comes from the selected OrtEpDevice);
            // passing it through would fail session creation on an unknown option.
            if (ClassicTrtProfileKeysToNvRtx.ContainsKey(key) ||
                string.Equals(key, "device_id", StringComparison.Ordinal))
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
    }

    /// <summary>
    /// Builds classic TensorRT EP provider options. Caller profile options pass through, but cache
    /// controls remain authoritative: the persistent cache is enabled only when the full hardware,
    /// driver, TensorRT binary, and ORT identity is known; otherwise caching is explicitly disabled.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> BuildTensorRtOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions) =>
        BuildTensorRtOptions(additionalTrtOptions, BuildClassicTensorRtEnvironmentFingerprint());

    internal static IReadOnlyDictionary<string, string> BuildTensorRtOptionsForModel(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        string? trtModelPath) =>
        BuildTensorRtOptions(additionalTrtOptions, BuildClassicTensorRtEnvironmentFingerprint(), trtModelPath);

    internal static IReadOnlyDictionary<string, string> BuildTensorRtOptionsAfterProviderLoad(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        string? trtModelPath,
        Action loadProvider,
        Func<string> resolveEnvironmentFingerprint)
    {
        loadProvider();
        return BuildTensorRtOptions(additionalTrtOptions, resolveEnvironmentFingerprint(), trtModelPath);
    }

    internal static IReadOnlyDictionary<string, string> BuildTensorRtOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        string environmentFingerprint) =>
        BuildTensorRtOptions(additionalTrtOptions, environmentFingerprint, trtModelPath: null);

    private static IReadOnlyDictionary<string, string> BuildTensorRtOptions(
        IReadOnlyDictionary<string, string>? additionalTrtOptions,
        string environmentFingerprint,
        string? trtModelPath)
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

        if (!HasCompleteClassicTensorRtCacheIdentity(environmentFingerprint))
        {
            // A process-specific TRT identity prevents cross-process reuse only when TRT itself
            // is unknown. Unknown GPU/driver/ORT identity is equally unsafe, so disable the cache
            // outright instead of creating a misleading persistent directory.
            trtOptions["trt_engine_cache_enable"] = "0";
            trtOptions.Remove("trt_engine_cache_path");
            trtOptions.Remove("trt_engine_cache_prefix");
            return trtOptions;
        }

        trtOptions["trt_engine_cache_enable"] = "1";
        trtOptions["trt_engine_cache_path"] = ResolveClassicTensorRtEngineCacheDirectory(environmentFingerprint);
        if (!string.IsNullOrWhiteSpace(trtModelPath))
        {
            trtOptions["trt_engine_cache_prefix"] = BuildClassicTensorRtModelCachePrefix(trtModelPath);
        }
        return trtOptions;
    }

    internal static string BuildClassicTensorRtModelCachePrefix(string modelPath)
    {
        string normalizedPath = Path.GetFullPath(modelPath);
        byte[] digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalizedPath));
        return $"trackdub-{Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant()}-";
    }

    internal static string ResolveClassicTensorRtEngineCacheDirectory() =>
        ResolveClassicTensorRtEngineCacheDirectory(BuildClassicTensorRtEnvironmentFingerprint());

    internal static string ResolveClassicTensorRtEngineCacheDirectory(string environmentFingerprint) =>
        Path.Join(
            ResolveEngineCacheRoot(),
            "classic-trt",
            SanitizeClassicTensorRtCacheKey(environmentFingerprint));

    // Classic engine-cache key components: GPU architecture bucket | exact GPU model name |
    // GPU driver version | TensorRT binary identity | ONNX Runtime version. This prevents engines
    // from being reused across two GPUs in the same architecture bucket (e.g. a moved /data volume).
    // The cache is disabled if any component is unavailable. The TensorRT-RTX EP version in
    // EpContextLoadPathResolver's fingerprint is deliberately excluded — classic TRT engines do
    // not depend on it. TensorRT identity is sourced from the mapped TensorRT core binary; an
    // unresolved identity uses a process-specific namespace so an unverified runtime never reuses
    // engines from another process.
    internal static string BuildClassicTensorRtEnvironmentFingerprint()
    {
        // The built-in classic TRT provider is not loaded until AppendExecutionProvider_Tensorrt,
        // but its cache directory must be selected before that call. Preload the ABI library so
        // the first session and every later process can identify the same native binary.
        TryLoadTensorRtIdentityLibrary();
        (string gpuArchitecture, string? driverVersion) = EpContextLoadPathResolver.CurrentHardware;
        string driver = string.IsNullOrWhiteSpace(driverVersion) ? "unknown" : driverVersion;
        string gpuModel = AcceleratorVramProbe.QueryNvmlGpuIdentity().DeviceName ?? "unknown";
        return $"{gpuArchitecture}|{gpuModel}|{driver}|trt-{ResolveTensorRtVersionIdentity()}|ort-{EpContextArtifact.HostOrtRuntimeVersion}";
    }

    internal static bool HasCompleteClassicTensorRtCacheIdentity(string environmentFingerprint)
    {
        string[] parts = environmentFingerprint.Split('|');
        if (parts.Length != 5 || parts.Any(static part => string.IsNullOrWhiteSpace(part)
            || string.Equals(part, "unknown", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        const string tensorRtHashPrefix = "trt-sha256-";
        if (!parts[3].StartsWith(tensorRtHashPrefix, StringComparison.Ordinal)
            || parts[3].Length != tensorRtHashPrefix.Length + 64
            || !parts[3].AsSpan(tensorRtHashPrefix.Length).ToArray().All(Uri.IsHexDigit))
        {
            return false;
        }

        return parts[4].StartsWith("ort-", StringComparison.Ordinal)
            && parts[4].Length > "ort-".Length
            && !string.Equals(parts[4], "ort-unknown", StringComparison.OrdinalIgnoreCase);
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

    private static readonly string UnresolvedTensorRtIdentity = $"unknown-process-{Guid.NewGuid():N}";
    private static readonly ConcurrentDictionary<string, string> LoadedTensorRtBinaryIdentities = new(StringComparer.Ordinal);

    private static bool TryLoadTensorRtIdentityLibrary()
    {
        // The current server image ships TensorRT 10. On an unsupported major version, keep the
        // identity unresolved and let the EP append path proceed without cross-process cache reuse.
        string libraryName = OperatingSystem.IsLinux()
            ? "libnvinfer.so.10"
            : OperatingSystem.IsWindows() ? "nvinfer.dll" : string.Empty;
        if (libraryName.Length == 0)
        {
            return false;
        }

        lock (TensorRtIdentityLibraryLoadLock)
        {
            if (_tensorRtIdentityLibraryHandle != IntPtr.Zero)
            {
                return true;
            }

            try
            {
                if (!NativeLibrary.TryLoad(libraryName, out IntPtr handle))
                {
                    return false;
                }

                // Keep a reference for process lifetime so ORT's provider append sees the same
                // module and the resolved identity cannot become stale before session creation.
                _tensorRtIdentityLibraryHandle = handle;
                return true;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException
                or FileLoadException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    internal static string ResolveTensorRtVersionIdentity()
    {
        try
        {
            using Process process = Process.GetCurrentProcess();
            return ResolveTensorRtVersionIdentity(
                process.Modules.Cast<ProcessModule>().Select(static module => module.FileName),
                static path => LoadedTensorRtBinaryIdentities.GetOrAdd(path, static loadedPath =>
                {
                    using FileStream stream = File.OpenRead(loadedPath);
                    return $"sha256-{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))}";
                }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
            or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return UnresolvedTensorRtIdentity;
        }
    }

    // Only mapped TensorRT core binaries count. Do not inspect PATH, LD_LIBRARY_PATH or
    // installed filenames: those can describe a different runtime from the one ORT loaded.
    // Hashing the actual binary also distinguishes builds with identical sonames. Missing or
    // ambiguous identities get a process-specific namespace, preventing cross-process reuse.
    internal static string ResolveTensorRtVersionIdentity(
        IEnumerable<string> loadedModulePaths,
        Func<string, string> readBinaryIdentity)
    {
        string[] candidates = loadedModulePaths
            .Where(static path => IsTensorRtCoreLibrary(Path.GetFileName(path)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return candidates.Length == 1 ? readBinaryIdentity(candidates[0]) : UnresolvedTensorRtIdentity;
    }

    private static bool IsTensorRtCoreLibrary(string name) =>
        name.Equals("libnvinfer.so", StringComparison.Ordinal)
        || name.StartsWith("libnvinfer.so.", StringComparison.Ordinal)
        || name.Equals("nvinfer.dll", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith("nvinfer_", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && name.Length > 12 && name[8..^4].All(char.IsDigit));

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

    // TensorRT-RTX runtime caches are not portable across runtime versions, so each pinned
    // EP ABI + runtime lineage gets its own directory and an upgrade never feeds old kernels in.
    internal static string ResolveTensorRtRtxRuntimeCachePath() =>
        Path.Join(ResolveEngineCacheRoot(), "trt-rtx", TensorRtRtxProviderConstants.BundledFingerprintVersion);

    private static string ResolveEngineCacheRoot()
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

}
