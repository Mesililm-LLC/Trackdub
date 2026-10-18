using Trackdub.Domain.Benchmarking;

namespace Trackdub.Domain.Tests;

public sealed class BenchmarkSamplingPolicyTests
{
    [Theory]
    [InlineData(null, null, 10, 3000)]
    [InlineData(1, null, 1, 0)]
    [InlineData(5, null, 5, 0)]
    [InlineData(1, 3000, 1, 3000)]
    [InlineData(null, 0, 10, 0)]
    [InlineData(null, 100, 10, 100)]
    [InlineData(1, 0, 1, 0)]
    public void Resolve_DistinguishesAbsentFlagsFromExplicitCountsAndZeroDuration(
        int? runs, int? duration, int expectedCount, int expectedDuration)
    {
        BenchmarkSamplingPolicy result = BenchmarkSamplingPolicy.Resolve(runs, duration);
        Assert.Equal(expectedCount, result.MinimumSamples);
        Assert.Equal(expectedDuration, result.MinimumMeasurementMilliseconds);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-1, null)]
    [InlineData(null, -1)]
    public void Resolve_RejectsInvalidLimits(int? runs, int? duration)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkSamplingPolicy.Resolve(runs, duration));
    }
}
