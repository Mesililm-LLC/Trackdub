using Trackdub.Benchmarks.Metrics;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class PercentileCalculatorTests
{
    [Fact]
    public void Calculate_P95UsesSharedType7Definition()
    {
        LatencyStatistics result = PercentileCalculator.Calculate([10, 20, 30, 40]);
        Assert.Equal(38.5, result.P95Milliseconds);
    }

    [Fact]
    public void Calculate_WhenSamplesNull_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => PercentileCalculator.Calculate(null!));
    }

    [Fact]
    public void Calculate_WhenSamplesEmpty_ReturnsZeroStatistics()
    {
        LatencyStatistics stats = PercentileCalculator.Calculate([]);

        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0.0, stats.MinMilliseconds);
        Assert.Equal(0.0, stats.MaxMilliseconds);
        Assert.Equal(0.0, stats.MeanMilliseconds);
        Assert.Equal(0.0, stats.P50Milliseconds);
        Assert.Equal(0.0, stats.P90Milliseconds);
        Assert.Equal(0.0, stats.P99Milliseconds);
        Assert.Equal(0.0, stats.ThroughputUnitsPerSecond);
    }

    [Fact]
    public void Calculate_SingleElement_AllPercentilesMatchSingleValue()
    {
        // N = 1: position = p * 0 = 0. left = 0, right = 0.
        double[] samples = [42.5];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(42.5, stats.MinMilliseconds);
        Assert.Equal(42.5, stats.MaxMilliseconds);
        Assert.Equal(42.5, stats.MeanMilliseconds);
        Assert.Equal(42.5, stats.P50Milliseconds);
        Assert.Equal(42.5, stats.P90Milliseconds);
        Assert.Equal(42.5, stats.P99Milliseconds);
        Assert.Equal(0.0, stats.ThroughputUnitsPerSecond);
    }

    [Fact]
    public void Calculate_TwoElements_ComputesInterpolatedPercentiles()
    {
        // N = 2: N - 1 = 1.
        // P50: pos = 0.5 * 1 = 0.5 => 10.0 + 0.5 * 10.0 = 15.0
        // P90: pos = 0.9 * 1 = 0.9 => 10.0 + 0.9 * 10.0 = 19.0
        // P99: pos = 0.99 * 1 = 0.99 => 10.0 + 0.99 * 10.0 = 19.9
        double[] samples = [10.0, 20.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(2, stats.SampleCount);
        Assert.Equal(10.0, stats.MinMilliseconds);
        Assert.Equal(20.0, stats.MaxMilliseconds);
        Assert.Equal(15.0, stats.MeanMilliseconds);
        Assert.Equal(15.0, stats.P50Milliseconds, 4);
        Assert.Equal(19.0, stats.P90Milliseconds, 4);
        Assert.Equal(19.9, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_OddLengthArray_ExactMedianAndInterpolatedTails()
    {
        // N = 5: N - 1 = 4.
        // P50: pos = 0.50 * 4 = 2.0 => X[2] = 30.0
        // P90: pos = 0.90 * 4 = 3.6 => X[3] + 0.6 * (X[4] - X[3]) = 40.0 + 0.6 * 10.0 = 46.0
        // P99: pos = 0.99 * 4 = 3.96 => X[3] + 0.96 * (X[4] - X[3]) = 40.0 + 0.96 * 10.0 = 49.6
        double[] samples = [10.0, 20.0, 30.0, 40.0, 50.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(5, stats.SampleCount);
        Assert.Equal(10.0, stats.MinMilliseconds);
        Assert.Equal(50.0, stats.MaxMilliseconds);
        Assert.Equal(30.0, stats.MeanMilliseconds);
        Assert.Equal(30.0, stats.P50Milliseconds, 4);
        Assert.Equal(46.0, stats.P90Milliseconds, 4);
        Assert.Equal(49.6, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_ThreeItemsOddLength_ExactMedianAndInterpolatedTails()
    {
        // N = 3: N - 1 = 2.
        // P50: pos = 0.50 * 2 = 1.0 => X[1] = 200.0
        // P90: pos = 0.90 * 2 = 1.8 => X[1] + 0.8 * 100.0 = 280.0
        // P99: pos = 0.99 * 2 = 1.98 => X[1] + 0.98 * 100.0 = 298.0
        double[] samples = [100.0, 200.0, 300.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(3, stats.SampleCount);
        Assert.Equal(100.0, stats.MinMilliseconds);
        Assert.Equal(300.0, stats.MaxMilliseconds);
        Assert.Equal(200.0, stats.MeanMilliseconds);
        Assert.Equal(200.0, stats.P50Milliseconds, 4);
        Assert.Equal(280.0, stats.P90Milliseconds, 4);
        Assert.Equal(298.0, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_EvenLengthArray_InterpolatedMedianAndTails()
    {
        // N = 4: N - 1 = 3.
        // P50: pos = 0.50 * 3 = 1.5 => X[1] + 0.5 * 10.0 = 25.0
        // P90: pos = 0.90 * 3 = 2.7 => X[2] + 0.7 * 10.0 = 37.0
        // P99: pos = 0.99 * 3 = 2.97 => X[2] + 0.97 * 10.0 = 39.7
        double[] samples = [10.0, 20.0, 30.0, 40.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(4, stats.SampleCount);
        Assert.Equal(10.0, stats.MinMilliseconds);
        Assert.Equal(40.0, stats.MaxMilliseconds);
        Assert.Equal(25.0, stats.MeanMilliseconds);
        Assert.Equal(25.0, stats.P50Milliseconds, 4);
        Assert.Equal(37.0, stats.P90Milliseconds, 4);
        Assert.Equal(39.7, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_TenElementArray_MatchesKnownDistribution()
    {
        // N = 10: N - 1 = 9.
        // P50: pos = 0.5 * 9 = 4.5 => 5.0 + 0.5 * 1.0 = 5.5
        // P90: pos = 0.9 * 9 = 8.1 => 9.0 + 0.1 * 1.0 = 9.1
        // P99: pos = 0.99 * 9 = 8.91 => 9.0 + 0.91 * 1.0 = 9.91
        double[] samples = [1.0, 2.0, 3.0, 4.0, 5.0, 6.0, 7.0, 8.0, 9.0, 10.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(10, stats.SampleCount);
        Assert.Equal(1.0, stats.MinMilliseconds);
        Assert.Equal(10.0, stats.MaxMilliseconds);
        Assert.Equal(5.5, stats.MeanMilliseconds);
        Assert.Equal(5.5, stats.P50Milliseconds, 4);
        Assert.Equal(9.1, stats.P90Milliseconds, 4);
        Assert.Equal(9.91, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_HundredElementArray_ComputesPrecisePercentiles()
    {
        // N = 100: N - 1 = 99.
        // P50: pos = 0.5 * 99 = 49.5 => 50.0 + 0.5 * 1.0 = 50.5
        // P90: pos = 0.9 * 99 = 89.1 => 90.0 + 0.1 * 1.0 = 90.1
        // P99: pos = 0.99 * 99 = 98.01 => 99.0 + 0.01 * 1.0 = 99.01
        double[] samples = Enumerable.Range(1, 100).Select(x => (double)x).ToArray();

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(100, stats.SampleCount);
        Assert.Equal(1.0, stats.MinMilliseconds);
        Assert.Equal(100.0, stats.MaxMilliseconds);
        Assert.Equal(50.5, stats.MeanMilliseconds);
        Assert.Equal(50.5, stats.P50Milliseconds, 4);
        Assert.Equal(90.1, stats.P90Milliseconds, 4);
        Assert.Equal(99.01, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_IdenticalValues_AllStatisticsEqual()
    {
        double[] samples = [100.0, 100.0, 100.0, 100.0, 100.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(5, stats.SampleCount);
        Assert.Equal(100.0, stats.MinMilliseconds);
        Assert.Equal(100.0, stats.MaxMilliseconds);
        Assert.Equal(100.0, stats.MeanMilliseconds);
        Assert.Equal(100.0, stats.P50Milliseconds);
        Assert.Equal(100.0, stats.P90Milliseconds);
        Assert.Equal(100.0, stats.P99Milliseconds);
    }

    [Fact]
    public void Calculate_HeavyOutliers_MedianIsResistantWhileMeanAndP99ReflectTail()
    {
        // N = 5: N - 1 = 4.
        // samples: 10, 10, 10, 10, 1000
        // mean: (40 + 1000) / 5 = 208.0
        // P50: pos = 2.0 => X[2] = 10.0
        // P90: pos = 3.6 => 10.0 + 0.6 * 990.0 = 604.0
        // P99: pos = 3.96 => 10.0 + 0.96 * 990.0 = 960.4
        double[] samples = [10.0, 10.0, 10.0, 10.0, 1000.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples);

        Assert.Equal(5, stats.SampleCount);
        Assert.Equal(10.0, stats.MinMilliseconds);
        Assert.Equal(1000.0, stats.MaxMilliseconds);
        Assert.Equal(208.0, stats.MeanMilliseconds);
        Assert.Equal(10.0, stats.P50Milliseconds);
        Assert.Equal(604.0, stats.P90Milliseconds, 4);
        Assert.Equal(960.4, stats.P99Milliseconds, 4);
    }

    [Fact]
    public void Calculate_UnsortedInput_ProducesIdenticalResultToSortedAndDoesNotMutateSource()
    {
        double[] unsorted = [50.0, 10.0, 40.0, 20.0, 30.0];
        double[] unsortedCopy = (double[])unsorted.Clone();
        double[] sorted = [10.0, 20.0, 30.0, 40.0, 50.0];

        LatencyStatistics statsUnsorted = PercentileCalculator.Calculate(unsorted);
        LatencyStatistics statsSorted = PercentileCalculator.Calculate(sorted);

        Assert.Equal(statsSorted, statsUnsorted);
        Assert.Equal(unsortedCopy, unsorted); // Source array not mutated
    }

    [Fact]
    public void Calculate_WithTotalUnitsAndDuration_ComputesThroughputAccurately()
    {
        // 60 audio seconds processed in 15 elapsed seconds => Throughput = 60 / 15 = 4.0 units/sec (4x real-time)
        double[] samples = [3000.0, 3000.0, 3000.0, 3000.0, 3000.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples, totalUnits: 60.0, totalDurationSeconds: 15.0);

        Assert.Equal(4.0, stats.ThroughputUnitsPerSecond, 4);
    }

    [Fact]
    public void Calculate_WithTotalUnitsAndZeroDuration_DerivesDurationFromSamplesSum()
    {
        // totalDurationSeconds <= 0 => duration = sum(samples) / 1000.0 = 5000 / 1000 = 5.0s
        // Throughput = 100 units / 5.0s = 20.0 units/sec
        double[] samples = [1000.0, 1000.0, 1000.0, 1000.0, 1000.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples, totalUnits: 100.0, totalDurationSeconds: 0.0);

        Assert.Equal(20.0, stats.ThroughputUnitsPerSecond, 4);
    }

    [Fact]
    public void Calculate_WithZeroUnitsAndZeroDuration_ReturnsZeroThroughput()
    {
        double[] samples = [100.0, 200.0];

        LatencyStatistics stats = PercentileCalculator.Calculate(samples, totalUnits: 0.0, totalDurationSeconds: 0.0);

        Assert.Equal(0.0, stats.ThroughputUnitsPerSecond);
    }

    [Fact]
    public void Calculate_ZeroOrNegativeDuration_GuardsAgainstDivisionByZero()
    {
        // Empty samples and zero duration: must not produce NaN or Infinity
        LatencyStatistics stats = PercentileCalculator.Calculate([], totalUnits: 50.0, totalDurationSeconds: 0.0);

        Assert.Equal(0.0, stats.ThroughputUnitsPerSecond);
        Assert.False(double.IsNaN(stats.ThroughputUnitsPerSecond));
        Assert.False(double.IsInfinity(stats.ThroughputUnitsPerSecond));
    }

    [Fact]
    public void Calculate_NegativeSample_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PercentileCalculator.Calculate([-5.0]));
    }

    [Fact]
    public void Calculate_NonFiniteSample_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PercentileCalculator.Calculate([double.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PercentileCalculator.Calculate([double.PositiveInfinity]));
    }

    [Fact]
    public void Calculate_NegativeUnits_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PercentileCalculator.Calculate([10.0], totalUnits: -1.0));
    }

    [Fact]
    public void Calculate_NegativeDuration_ThrowsArgumentOutOfRangeException()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PercentileCalculator.Calculate([10.0], totalDurationSeconds: -1.0));
    }
}
