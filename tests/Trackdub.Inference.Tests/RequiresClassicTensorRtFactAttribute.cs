using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Skips when the classic (built-in) TensorRT execution provider is not enabled in the
/// ONNX Runtime build loaded by the test host (e.g. a CPU-only ORT native wins the
/// copy-local race on a Windows dev host). The provider-options surface
/// (<see cref="OrtTensorRTProviderOptions"/>) throws at construction in that case, so
/// option-carrying assertions can only run where the EP is actually present.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class RequiresClassicTensorRtFactAttribute : FactAttribute
{
    public RequiresClassicTensorRtFactAttribute()
    {
        Skip = ResolveClassicTensorRtSkip();
    }

    internal static string? ResolveClassicTensorRtSkip()
    {
        try
        {
            return OrtEnv.Instance().GetAvailableProviders()
                .Contains("TensorrtExecutionProvider", StringComparer.Ordinal)
                ? null
                : "Classic TensorRT execution provider is not enabled in this ONNX Runtime build.";
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or TypeInitializationException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return $"Classic TensorRT execution provider could not be probed: {ex.Message}";
        }
    }
}
