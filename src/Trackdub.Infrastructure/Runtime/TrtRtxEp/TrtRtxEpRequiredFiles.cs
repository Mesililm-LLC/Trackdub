namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

/// <summary>
/// Required native files in a flat TensorRT RTX EP ABI plugin directory after extract.
/// </summary>
internal static class TrtRtxEpRequiredFiles
{
    private static readonly IReadOnlyList<string> s_linuxFiles =
    [
        "libonnxruntime_providers_nv_tensorrt_rtx.so",
        "libtensorrt_rtx.so",
        "libtensorrt_onnxparser_rtx.so",
    ];

    private static readonly IReadOnlyList<string> s_windowsFiles =
    [
        "onnxruntime_providers_nv_tensorrt_rtx.dll",
        "tensorrt_rtx_1_7.dll",
        "tensorrt_onnxparser_rtx_1_7.dll",
    ];

    public static IReadOnlyList<string> RequiredFileNames =>
        OperatingSystem.IsLinux() ? s_linuxFiles
        : OperatingSystem.IsWindows() ? s_windowsFiles
        : throw new PlatformNotSupportedException(
            "TensorRT RTX EP ABI plugin is not supported on this platform.");
}
