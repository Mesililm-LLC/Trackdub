namespace Trackdub.Inference.Onnx.Tests.Runtime;

#if WINDOWS
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
