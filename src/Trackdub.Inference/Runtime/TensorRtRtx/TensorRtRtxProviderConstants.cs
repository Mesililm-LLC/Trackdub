using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Runtime.TensorRtRtx;

public static class TensorRtRtxProviderConstants
{
    public const string PluginOrtExecutionProviderName = "NvTensorRTRTXExecutionProvider";

    public const string PluginLibraryFileNameWindows = "onnxruntime_providers_nv_tensorrt_rtx.dll";

    public const string PluginLibraryFileNameLinux = "libonnxruntime_providers_nv_tensorrt_rtx.so";

    public const string TensorRtRuntimeFileNameWindows = "tensorrt_rtx_1_6.dll";

    public const string TensorRtRuntimeFileNameLinux = "libtensorrt_rtx.so";

    public const string TensorRtOnnxParserFileNameWindows = "tensorrt_onnxparser_rtx_1_6.dll";

    public const string TensorRtOnnxParserFileNameLinux = "libtensorrt_onnxparser_rtx.so";

    public const string PluginDirectoryEnvironmentVariable = "TRACKDUB_TRT_RTX_EP_DIR";

    public const string CudaRuntimeBinDirectoryEnvironmentVariable = "TRACKDUB_CUDA_BIN_DIR";

    // NVIDIA publishes EP ABI assets per platform on different tags: v0.4.2 is Windows-only,
    // v0.4.0 is the newest linux-x86_64 cu13 asset. Both vendor TensorRT-RTX 1.6.1.
    public const string BundledVersionWindows = "0.4.2";

    public const string BundledVersionLinux = "0.4.0";

    public const string BundledCudaVariant = "cu13";

    /// <summary>
    /// TensorRT-RTX runtime vendored by the pinned EP ABI bundle. Tracked separately from the EP ABI
    /// version because the runtime lineage can move without the plugin version moving.
    /// </summary>
    public const string BundledTrtRtxRuntimeVersion = "1.6.1";

    public static string BundledVersion =>
        OperatingSystem.IsLinux() ? BundledVersionLinux : BundledVersionWindows;

    /// <summary>
    /// Version identity for anything that must invalidate with the engine cache (smoke verdicts,
    /// EP-context stamps): changes when either the EP ABI plugin or the TRT-RTX runtime changes.
    /// </summary>
    public static string BundledFingerprintVersion =>
        $"{BundledVersion}+{BundledCudaVariant}+trt-rtx-{BundledTrtRtxRuntimeVersion}";

    public const string NativeOrtExecutionProviderName = "TensorrtExecutionProvider";

    public static string ProviderIdPluginEpAbi => TensorRtRtxProviderIds.PluginEpAbi;

    public static string ProviderIdNative => TensorRtRtxProviderIds.Native;

    public static string PluginLibraryFileName =>
        OperatingSystem.IsLinux()
            ? PluginLibraryFileNameLinux
            : PluginLibraryFileNameWindows;

    public static string TensorRtRuntimeFileName =>
        OperatingSystem.IsLinux()
            ? TensorRtRuntimeFileNameLinux
            : TensorRtRuntimeFileNameWindows;

    public static string TensorRtOnnxParserFileName =>
        OperatingSystem.IsLinux()
            ? TensorRtOnnxParserFileNameLinux
            : TensorRtOnnxParserFileNameWindows;

    public static IReadOnlyList<string> RequiredPluginFileNames =>
    [
        PluginLibraryFileName,
        TensorRtRuntimeFileName,
        TensorRtOnnxParserFileName
    ];

    public const string LinuxInstallHint =
        "Use Install in Model Manager to download NVIDIA's TensorRT-RTX EP ABI plugin (v" + BundledVersionLinux + " " + BundledCudaVariant
        + ") through the LinML catalog, then refresh readiness. "
        + "The plugin ships its own CUDA 13 runtime (libcudart.so.13). "
        + "To use your own build instead, set " + PluginDirectoryEnvironmentVariable + ".";

    public const string WindowsInstallHint =
        "Use Install in Model Manager to download TensorRT-RTX-EP-ABI v" + BundledVersionWindows + " " + BundledCudaVariant
        + " for win-x64, or run tools/dev/Fetch-TrtRtxEp.ps1, then refresh readiness. "
        + "No CUDA Toolkit is required: the CUDA runtime is statically linked into the TensorRT-RTX 1.6 bundle.";

    public static string GetDefaultInstallDirectory(string userDataRoot, string runtimeIdentifier) =>
        Path.Join(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(userDataRoot)),
            "Providers",
            "trt-rtx",
            BundledVersion,
            BundledCudaVariant,
            runtimeIdentifier);

    /// <summary>
    /// True when <paramref name="candidateDirectory"/> is a different version/variant directory under
    /// the same managed <c>Providers/trt-rtx</c> root as <paramref name="defaultInstallDirectory"/>.
    /// </summary>
    public static bool IsSupersededManagedInstallDirectory(string? candidateDirectory, string? defaultInstallDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidateDirectory) || string.IsNullOrWhiteSpace(defaultInstallDirectory))
        {
            return false;
        }

        try
        {
            string candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidateDirectory));
            string current = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(defaultInstallDirectory)));

            // <root>/<version>/<cudaVariant>/<rid>
            string? managedRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(current)));
            if (string.IsNullOrEmpty(managedRoot) ||
                string.Equals(candidate, current, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string? candidateRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(candidate)));
            return string.Equals(candidateRoot, managedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
