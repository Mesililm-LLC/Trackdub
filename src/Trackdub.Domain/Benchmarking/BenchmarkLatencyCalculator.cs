namespace Trackdub.Domain.Benchmarking;

/// <summary>Type 7 latency percentiles shared by inference and benchmark reporting.</summary>
public static class BenchmarkLatencyCalculator
{
    public static BenchmarkLatencyStatistics? Calculate(IReadOnlyList<double> samplesMilliseconds)
    {
        ArgumentNullException.ThrowIfNull(samplesMilliseconds);
        if (samplesMilliseconds.Count == 0)
        {
            return null;
        }

        var sorted = new double[samplesMilliseconds.Count];
        double mean = 0;
        for (int index = 0; index < sorted.Length; index++)
        {
            double sample = samplesMilliseconds[index];
            ValidateSample(sample);
            sorted[index] = sample;
            // An incremental mean avoids overflowing the sum of finite durations.
            mean += (sample - mean) / (index + 1);
        }

        Array.Sort(sorted);
        return new BenchmarkLatencyStatistics(
            sorted.Length, sorted[0], sorted[^1], mean,
            Interpolate(sorted, 0.5), Interpolate(sorted, 0.9),
            Interpolate(sorted, 0.95), Interpolate(sorted, 0.99));
    }

    public static double? CalculatePercentile(ReadOnlySpan<double> sortedSamples, double fraction)
    {
        if (!double.IsFinite(fraction) || fraction < 0 || fraction > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fraction));
        }

        for (int index = 0; index < sortedSamples.Length; index++)
        {
            ValidateSample(sortedSamples[index]);
            if (index > 0 && sortedSamples[index] < sortedSamples[index - 1])
            {
                throw new ArgumentException("Percentile input must be sorted.", nameof(sortedSamples));
            }
        }

        return sortedSamples.IsEmpty ? null : Interpolate(sortedSamples, fraction);
    }

    private static double Interpolate(ReadOnlySpan<double> sorted, double fraction)
    {
        double position = fraction * (sorted.Length - 1);
        int left = (int)Math.Floor(position);
        int right = (int)Math.Ceiling(position);
        return sorted[left] + (sorted[right] - sorted[left]) * (position - left);
    }

    private static void ValidateSample(double sample)
    {
        if (!double.IsFinite(sample) || sample < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sample), sample, "Latency must be finite and non-negative.");
        }
    }
}
