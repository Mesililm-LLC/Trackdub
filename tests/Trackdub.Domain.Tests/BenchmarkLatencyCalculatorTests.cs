using Trackdub.Domain.Benchmarking;

namespace Trackdub.Domain.Tests;

public sealed class BenchmarkLatencyCalculatorTests
{
    [Fact]
    public void Calculate_UsesType7ForEveryReportedPercentile()
    {
        double[] samples = [40, 10, 30, 20];

        BenchmarkLatencyStatistics result = Assert.IsType<BenchmarkLatencyStatistics>(
            BenchmarkLatencyCalculator.Calculate(samples));

        Assert.Equal(4, result.SampleCount);
        Assert.Equal(10, result.MinMilliseconds);
        Assert.Equal(40, result.MaxMilliseconds);
        Assert.Equal(25, result.MeanMilliseconds);
        Assert.Equal(25, result.P50Milliseconds);
        Assert.Equal(37, result.P90Milliseconds, 8);
        Assert.Equal(38.5, result.P95Milliseconds, 8);
        Assert.Equal(39.7, result.P99Milliseconds, 8);
        Assert.Equal(new double[] { 40, 10, 30, 20 }, samples);
    }

    [Fact]
    public void Calculate_EmptyInputHasNoFabricatedStatistics()
    {
        Assert.Null(BenchmarkLatencyCalculator.Calculate([]));
        Assert.Null(BenchmarkLatencyCalculator.CalculatePercentile([], 0.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8.25)]
    public void Calculate_OneSampleAndDuplicatesHaveIdenticalPercentiles(double sample)
    {
        BenchmarkLatencyStatistics result = Assert.IsType<BenchmarkLatencyStatistics>(
            BenchmarkLatencyCalculator.Calculate([sample, sample, sample]));

        Assert.Equal(sample, result.MinMilliseconds);
        Assert.Equal(sample, result.MaxMilliseconds);
        Assert.Equal(sample, result.MeanMilliseconds);
        Assert.Equal(sample, result.P50Milliseconds);
        Assert.Equal(sample, result.P90Milliseconds);
        Assert.Equal(sample, result.P95Milliseconds);
        Assert.Equal(sample, result.P99Milliseconds);

        Assert.Equal(sample, BenchmarkLatencyCalculator.CalculatePercentile([sample], 0.99));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Calculate_RejectsInvalidSamples(double sample)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BenchmarkLatencyCalculator.Calculate([1, sample]));
    }

    [Fact]
    public void Calculate_RejectsNullInput()
    {
        Assert.Throws<ArgumentNullException>(() => BenchmarkLatencyCalculator.Calculate(null!));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void CalculatePercentile_RequiresFiniteFraction(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BenchmarkLatencyCalculator.CalculatePercentile([1, 2], fraction));
    }

    [Fact]
    public void Calculate_DoesNotOverflowMeanOfFiniteSamples()
    {
        BenchmarkLatencyStatistics result = Assert.IsType<BenchmarkLatencyStatistics>(
            BenchmarkLatencyCalculator.Calculate([double.MaxValue, double.MaxValue]));

        Assert.Equal(double.MaxValue, result.MeanMilliseconds);
        Assert.Equal(double.MaxValue, result.P99Milliseconds);
    }
}
