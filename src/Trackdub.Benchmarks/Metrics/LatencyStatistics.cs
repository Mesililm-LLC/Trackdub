namespace Trackdub.Benchmarks.Metrics;

/// <summary>
/// Statistical summary of latency samples and throughput for an operation or pipeline stage.
/// </summary>
/// <param name="MinMilliseconds">Minimum observed latency in milliseconds.</param>
/// <param name="MaxMilliseconds">Maximum observed latency in milliseconds.</param>
/// <param name="MeanMilliseconds">Arithmetic mean latency in milliseconds.</param>
/// <param name="P50Milliseconds">50th percentile (median) latency in milliseconds.</param>
/// <param name="P90Milliseconds">90th percentile latency in milliseconds.</param>
/// <param name="P99Milliseconds">99th percentile latency in milliseconds.</param>
/// <param name="ThroughputUnitsPerSecond">Throughput in units per second (e.g. audio seconds/second or items/second).</param>
/// <param name="SampleCount">Total number of samples measured.</param>
/// <param name="P95Milliseconds">95th percentile, or null when unavailable in legacy statistics.</param>
public sealed record LatencyStatistics(
    double MinMilliseconds,
    double MaxMilliseconds,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P90Milliseconds,
    double P99Milliseconds,
    double ThroughputUnitsPerSecond,
    int SampleCount,
    double? P95Milliseconds = null)
{
    /// <summary>
    /// Represents an empty statistics instance with all metrics set to 0.
    /// </summary>
    public static LatencyStatistics Empty { get; } = new(0d, 0d, 0d, 0d, 0d, 0d, 0d, 0);
}
