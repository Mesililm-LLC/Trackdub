using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Tests;

public sealed class TensorRtRtxDriverWorkaroundsTests
{
    [Theory]
    [InlineData("617.14", 617)]
    [InlineData("615.99", 615)]
    [InlineData("580.88", 580)]
    [InlineData("590.48.01", 590)]
    [InlineData("32.0.16.1714", 617)]
    [InlineData("32.0.15.8180", 581)]
    [InlineData("32.0.16.1599", 615)]
    [InlineData("31.0.15.0123", 501)]
    public void TryParseNvidiaDriverBranch_reads_nvidia_and_windows_registry_forms(string version, int expected)
    {
        Assert.True(TensorRtRtxDriverWorkarounds.TryParseNvidiaDriverBranch(version, out int branch));
        Assert.Equal(expected, branch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("12.4")]
    [InlineData("32.0.x.1714")]
    public void TryParseNvidiaDriverBranch_rejects_unrecognized_versions(string? version)
    {
        Assert.False(TensorRtRtxDriverWorkarounds.TryParseNvidiaDriverBranch(version, out _));
    }

    [Theory]
    [InlineData("615.99", true, true)]
    [InlineData("32.0.15.8180", true, true)]
    [InlineData("616.00", true, false)]
    [InlineData("32.0.16.1714", true, false)]
    [InlineData("580.88", false, false)]
    [InlineData(null, true, false)]
    public void Apply_caps_shared_memory_only_on_affected_windows_drivers(string? driver, bool isWindows, bool expectCap)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);

        TensorRtRtxDriverWorkarounds.Apply(options, driver, isWindows);

        Assert.Equal(expectCap, options.ContainsKey(TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey));
        if (expectCap)
        {
            Assert.Equal("101376", options[TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey]);
        }
    }

    [Fact]
    public void Apply_keeps_a_caller_supplied_shared_memory_limit()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey] = "65536",
        };

        TensorRtRtxDriverWorkarounds.Apply(options, "600.10", isWindows: true);

        Assert.Equal("65536", options[TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("202752")]
    [InlineData("not-a-number")]
    [InlineData("-1")]
    public void Apply_clamps_caller_limits_that_leave_the_engine_uncapped(string requested)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey] = requested,
        };

        TensorRtRtxDriverWorkarounds.Apply(options, "600.10", isWindows: true);

        Assert.Equal("101376", options[TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey]);
    }

    [Fact]
    public void Apply_leaves_caller_limits_untouched_on_unaffected_drivers()
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey] = "0",
        };

        TensorRtRtxDriverWorkarounds.Apply(options, "617.14", isWindows: true);

        Assert.Equal("0", options[TensorRtRtxDriverWorkarounds.MaxSharedMemSizeOptionKey]);
    }
}
