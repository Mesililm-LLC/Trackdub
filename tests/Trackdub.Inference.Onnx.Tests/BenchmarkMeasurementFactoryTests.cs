using Trackdub.Domain.Benchmarking;
using Trackdub.Inference.Onnx.Benchmarking;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class BenchmarkMeasurementFactoryTests
{
    [Fact]
    public void Create_UsesActualWallTimeForThroughputAndSampleDurationsForLatency()
    {
        var sampling = new BenchmarkSamplingResult(50, 8, 200, [10, 20, 30], 100, true, null);

        var result = BenchmarkMeasurementFactory.Create(
            sampling, 500, BenchmarkThroughputUnit.AudioSecondsPerSecond, 0.032);

        Assert.Equal(500, result.ColdLoadMilliseconds);
        Assert.Equal(50, result.FirstRunMilliseconds);
        Assert.Equal(3, result.SampleCount);
        Assert.Equal(100, result.MeasuredWallTimeMilliseconds);
        Assert.Equal(20, result.WarmLatencyAverageMilliseconds);
        Assert.Equal(20, result.WarmLatencyP50Milliseconds);
        Assert.Equal(28, result.WarmLatencyP90Milliseconds);
        Assert.Equal(29, result.WarmLatencyP95Milliseconds);
        Assert.Equal(29.8, result.WarmLatencyP99Milliseconds!.Value, 8);
        Assert.Equal(30, result.PassesPerSecond);
        Assert.Equal(0.96, result.ThroughputUnitsPerSecond!.Value, 8);
        Assert.Equal(0.625, result.RealTimeFactorAverage);
        Assert.Equal(BenchmarkThroughputUnit.AudioSecondsPerSecond, result.ThroughputUnit);
        Assert.Equal(new double[] { 10, 20, 30 }, result.LatencySamplesMilliseconds);
    }

    [Fact]
    public void Create_IncompletePolicyRetainsSamplesButNotQualifiedAggregates()
    {
        var sampling = new BenchmarkSamplingResult(
            50, 8, 200, [10, 20], 100, false, new InvalidOperationException("failed"));

        var result = BenchmarkMeasurementFactory.Create(sampling, 500, BenchmarkThroughputUnit.PassesPerSecond);

        Assert.Equal(2, result.SampleCount);
        Assert.Equal(new double[] { 10, 20 }, result.LatencySamplesMilliseconds);
        Assert.Null(result.WarmLatencyAverageMilliseconds);
        Assert.Null(result.WarmLatencyMinimumMilliseconds);
        Assert.Null(result.WarmLatencyMaximumMilliseconds);
        Assert.Null(result.WarmLatencyP50Milliseconds);
        Assert.Null(result.WarmLatencyP95Milliseconds);
        Assert.Null(result.PassesPerSecond);
        Assert.Null(result.ThroughputUnitsPerSecond);
        Assert.Null(result.RealTimeFactorAverage);
    }

    [Fact]
    public void Create_ZeroWallTimeDoesNotInventInfiniteThroughput()
    {
        var sampling = new BenchmarkSamplingResult(0, 1, 0, [0], 0, true, null);

        var result = BenchmarkMeasurementFactory.Create(sampling, 0, BenchmarkThroughputUnit.PassesPerSecond);

        Assert.Equal(0, result.WarmLatencyAverageMilliseconds);
        Assert.Null(result.PassesPerSecond);
        Assert.Null(result.ThroughputUnitsPerSecond);
    }

    [Fact]
    public void Create_AudioUnitRequiresKnownPositiveWindow()
    {
        var sampling = new BenchmarkSamplingResult(1, 1, 1, [1], 1, true, null);
        Assert.Throws<ArgumentException>(() => BenchmarkMeasurementFactory.Create(
            sampling, 1, BenchmarkThroughputUnit.AudioSecondsPerSecond));
    }
}
