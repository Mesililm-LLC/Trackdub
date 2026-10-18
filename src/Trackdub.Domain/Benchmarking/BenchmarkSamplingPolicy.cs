namespace Trackdub.Domain.Benchmarking;

/// <summary>
/// Resolved minimum sample count and measurement-duration floor from optional CLI flags.
/// An explicit run count without a duration stays an exact-count smoke run; defaults
/// only apply when the flag is absent, so an explicit zero duration is honored.
/// </summary>
public sealed record BenchmarkSamplingPolicy(
    int MinimumSamples,
    int MinimumMeasurementMilliseconds)
{
    public const int DefaultMinimumSamples = 10;
    public const int DefaultMinimumMeasurementMilliseconds = 3000;

    public static BenchmarkSamplingPolicy Resolve(int? runs, int? durationMilliseconds)
    {
        if (runs is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runs), runs, "Run count must be a positive integer.");
        }

        if (durationMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(durationMilliseconds), durationMilliseconds, "Duration must be non-negative milliseconds.");
        }

        int minimumSamples = runs ?? DefaultMinimumSamples;
        int minimumDuration = durationMilliseconds
            ?? (runs.HasValue ? 0 : DefaultMinimumMeasurementMilliseconds);
        return new BenchmarkSamplingPolicy(minimumSamples, minimumDuration);
    }
}
