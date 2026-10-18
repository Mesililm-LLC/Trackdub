namespace Trackdub.Domain.Benchmarking;

/// <summary>Statistics over completed inference-pass durations, without startup or warmup.</summary>
public sealed record BenchmarkLatencyStatistics(
    int SampleCount,
    double MinMilliseconds,
    double MaxMilliseconds,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P90Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds);
