using Trackdub.Benchmarks.Metrics;

namespace Trackdub.Benchmarks.Tests.Metrics;

/// <summary>
/// Constant snapshot source for collector concurrency tests. The default provider refreshes the
/// whole OS process table per event (~20 ms on Windows), which made lock-correctness tests take
/// tens of seconds without exercising anything the real-capture stress tests don't already cover.
/// </summary>
internal static class FixedResourceSnapshot
{
    private static readonly ResourceTelemetrySnapshot Snapshot = new(100_000_000, 110_000_000, 20_000_000, 2, 1, 0);

    public static ResourceTelemetrySnapshot? Provide() => Snapshot;
}
