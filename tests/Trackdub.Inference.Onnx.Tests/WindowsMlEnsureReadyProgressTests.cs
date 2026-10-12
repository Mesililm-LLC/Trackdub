using Trackdub.Inference.Onnx.WindowsMl;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class WindowsMlEnsureReadyProgressTests
{
    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(50.0, 0.5)]
    [InlineData(100.0, 1.0)]
    [InlineData(33.5, 0.335)]
    public void ReportPercent_normalizes_winrt_range_to_unit_interval(double rawPercent, double expected)
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, rawPercent);

        Assert.Equal(new[] { expected }, sink.Reports);
    }

    [Fact]
    public void ReportPercent_emits_zero_for_raw_zero()
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, 0.0);

        Assert.Equal(new[] { 0.0 }, sink.Reports);
    }

    [Fact]
    public void ReportPercent_emits_one_for_raw_hundred()
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, 100.0);

        Assert.Equal(new[] { 1.0 }, sink.Reports);
    }

    [Fact]
    public void ReportPercent_reports_each_call_independently()
    {
        var sink = new RecordingProgress();
        WindowsMlEnsureReady.ReportPercent(sink, 10.0);
        WindowsMlEnsureReady.ReportPercent(sink, 25.0);
        WindowsMlEnsureReady.ReportPercent(sink, 75.0);
        WindowsMlEnsureReady.ReportPercent(sink, 100.0);

        Assert.Equal(new[] { 0.10, 0.25, 0.75, 1.0 }, sink.Reports);
    }

    private sealed class RecordingProgress : IProgress<double>
    {
        public List<double> Reports { get; } = new();

        public void Report(double value) => Reports.Add(value);
    }
}
