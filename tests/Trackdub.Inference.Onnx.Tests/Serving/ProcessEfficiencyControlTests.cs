using Trackdub.Contracts.Serving;

namespace Trackdub.Inference.Onnx.Tests.Serving;

// EcoQoS throttling is PROCESS-GLOBAL state. These tests and Runtime.WindowsEcoQosTests both
// mutate it; xunit runs distinct collections in parallel, so a parallel finally-block Disable
// in the other class can land between this test's enable and its verified read-back (observed
// as a flake 2026-10-12). One named collection serializes them with each other — nothing else.
[Collection("ProcessEcoQosState")]
public sealed class ProcessEfficiencyControlTests
{
    [Fact]
    public void TryEnableEcoQos_round_trips_with_verified_read_back()
    {
        IProcessEfficiencyControl control = new Trackdub.Inference.Onnx.Serving.ProcessEfficiencyControl();
        try
        {
            (bool applied, string? error) = control.TryEnableEcoQos();
#if WINDOWS
            Assert.True(applied, error);
            Assert.True(control.IsEcoQosEnabled);

            // Return to OS-managed scheduling through the underlying helper, then confirm the
            // control's read-back sees the change (no cached state).
            (applied, error) = Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.DisableExecutionSpeedThrottling();
            Assert.True(applied, error);
            Assert.False(control.IsEcoQosEnabled);
#else
            // Portable leg: the implementation reports unsupported instead of pretending.
            Assert.False(applied);
            Assert.NotNull(error);
            Assert.False(control.IsEcoQosEnabled);
#endif
        }
        finally
        {
#if WINDOWS
            // Always leave the test process at OS-managed (neutral) throttling.
            Trackdub.Inference.Onnx.Runtime.WindowsEcoQos.DisableExecutionSpeedThrottling();
#endif
        }
    }
}
