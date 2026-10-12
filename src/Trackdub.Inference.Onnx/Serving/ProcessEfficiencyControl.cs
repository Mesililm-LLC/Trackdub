using Trackdub.Contracts.Serving;

namespace Trackdub.Inference.Onnx.Serving;

/// <summary>
/// <see cref="IProcessEfficiencyControl"/> over <see cref="Runtime.WindowsEcoQos"/>. The
/// portable TFM excludes <c>Runtime/WindowsEcoQos.cs</c> from compilation, so it reports the
/// mechanism as unsupported there instead of pretending.
/// </summary>
internal sealed class ProcessEfficiencyControl : IProcessEfficiencyControl
{
    public (bool Applied, string? Error) TryEnableEcoQos() =>
#if WINDOWS
        Runtime.WindowsEcoQos.EnableExecutionSpeedThrottling();
#else
        (false, "EcoQoS requires the Windows build of the inference runtime.");
#endif

    public bool IsEcoQosEnabled =>
#if WINDOWS
        Runtime.WindowsEcoQos.IsExecutionSpeedThrottled;
#else
        false;
#endif
}
