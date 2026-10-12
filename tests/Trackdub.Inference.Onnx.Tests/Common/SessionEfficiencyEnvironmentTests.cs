namespace Trackdub.Inference.Onnx.Tests.Common;

public sealed class SessionEfficiencyEnvironmentTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData("true", false)]   // strictly "1", matching TRACKDUB_RESIDENT_WARMUP=0 convention
    public void IsDisableCpuMemArenaEnabled_accepts_only_exact_1(string? value, bool expected)
    {
        Assert.Equal(expected, Trackdub.Inference.Onnx.Common.SessionEfficiencyEnvironment
            .IsDisableCpuMemArenaEnabled(value));
    }
}
