namespace Trackdub.Inference.Onnx.Tests.Runtime;

// EcoQoS throttling is PROCESS-GLOBAL state; shares the ProcessEfficiencyControlTests
// collection so the two classes never interleave enable/disable/read-back (flake observed
// 2026-10-12 when xunit ran them in parallel collections).
#if WINDOWS
[Collection("ProcessEcoQosState")]
public sealed class WindowsEcoQosTests
{
    [Fact]
    public void Enable_and_disable_round_trip_with_read_back()
    {
        try
        {
            (bool applied, string? error) = Trackdub.Inference.Onnx.Runtime.WindowsEcoQos
                .EnableExecutionSpeedThrottling();
            Assert.True(applied, error);
            Assert.True(Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.IsExecutionSpeedThrottled);

            (applied, error) = Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.DisableExecutionSpeedThrottling();
            Assert.True(applied, error);
            Assert.False(Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.IsExecutionSpeedThrottled);
        }
        finally
        {
            // Always leave the test process at OS-managed (neutral) throttling.
            Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.DisableExecutionSpeedThrottling();
        }
    }
}
#endif
