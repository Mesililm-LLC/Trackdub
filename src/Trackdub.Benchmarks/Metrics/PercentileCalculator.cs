using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Metrics;

/// <summary>
/// Provides statistical percentile, mean, and throughput calculations for benchmark timing distributions.
/// Uses Type 7 linear interpolation rank formula consistent with Trackdub media quality analysis.
/// </summary>
public static class PercentileCalculator
{
    /// <summary>
    /// Computes summary statistics including min, max, mean, percentiles (P50, P90, P99),
    /// and throughput in units per second.
    /// </summary>
    /// <param name="samplesMilliseconds">Collection of latency durations in milliseconds.</param>
    /// <param name="totalUnits">Optional unit count (e.g. audio duration in seconds or item count). If &lt;= 0, throughput will be 0.</param>
    /// <param name="totalDurationSeconds">Optional total wall-clock duration in seconds. If &lt;= 0, defaults to sum of sample durations.</param>
    /// <returns>A populated <see cref="LatencyStatistics"/> instance, or <see cref="LatencyStatistics.Empty"/> if empty.</returns>
    public static LatencyStatistics Calculate(
        IReadOnlyList<double> samplesMilliseconds,
        double totalUnits = 0,
        double totalDurationSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(samplesMilliseconds);
        ArgumentOutOfRangeException.ThrowIfNegative(totalUnits);
        ArgumentOutOfRangeException.ThrowIfNegative(totalDurationSeconds);

        if (samplesMilliseconds.Count == 0)
        {
            return LatencyStatistics.Empty;
        }

        double sum = 0d;
        double[] sorted = new double[samplesMilliseconds.Count];
        for (int i = 0; i < samplesMilliseconds.Count; i++)
        {
            double val = samplesMilliseconds[i];
            if (!double.IsFinite(val) || val < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(samplesMilliseconds),
                    val,
                    $"Latency sample at index {i} must be a non-negative, finite number.");
            }

            sorted[i] = val;
            sum += val;
        }

        Array.Sort(sorted);

        double min = sorted[0];
        double max = sorted[^1];
        double mean = sum / sorted.Length;
        double p50 = CalculatePercentile(sorted, 0.50d);
        double p90 = CalculatePercentile(sorted, 0.90d);
        double p95 = CalculatePercentile(sorted, 0.95d);
        double p99 = CalculatePercentile(sorted, 0.99d);

        double throughput = 0d;
        if (totalUnits > 0)
        {
            double effectiveDurationSeconds = totalDurationSeconds > 0 ? totalDurationSeconds : (sum / 1000d);
            if (effectiveDurationSeconds > 0)
            {
                throughput = totalUnits / effectiveDurationSeconds;
            }
        }

        return new LatencyStatistics(
            MinMilliseconds: min,
            MaxMilliseconds: max,
            MeanMilliseconds: mean,
            P50Milliseconds: p50,
            P90Milliseconds: p90,
            P99Milliseconds: p99,
            ThroughputUnitsPerSecond: throughput,
            SampleCount: sorted.Length,
            P95Milliseconds: p95);
    }

    /// <summary>
    /// Calculates the percentile of an already sorted span of values using linear interpolation rank formula.
    /// Percentile can be provided as a fraction [0.0, 1.0] or percentage [0.0, 100.0].
    /// </summary>
    /// <param name="sortedSamples">Sorted ascending span of samples.</param>
    /// <param name="percentile">Percentile rank (e.g. 0.50 or 50.0 for median).</param>
    /// <returns>The linearly interpolated percentile value, or 0.0 if empty.</returns>
    public static double CalculatePercentile(ReadOnlySpan<double> sortedSamples, double percentile)
    {
        if (sortedSamples.Length == 0)
        {
            return 0d;
        }

        double p = percentile > 1d ? percentile / 100d : percentile;
        p = Math.Clamp(p, 0d, 1d);

        return BenchmarkLatencyCalculator.CalculatePercentile(sortedSamples, p) ?? 0d;
    }
}
