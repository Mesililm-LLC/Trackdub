using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Inference.Onnx.Benchmarking;

/// <summary>Assembles qualified statistics without substituting reciprocal latency for wall throughput.</summary>
internal static class BenchmarkMeasurementFactory
{
    internal static BenchmarkMeasurements Create(
        BenchmarkSamplingResult sampling,
        double sessionInitializationMilliseconds,
        BenchmarkThroughputUnit unit,
        double? audioSecondsPerPass = null,
        double? itemsPerPass = null)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        if (!double.IsFinite(sessionInitializationMilliseconds) || sessionInitializationMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sessionInitializationMilliseconds));
        }

        double unitsPerPass = unit switch
        {
            BenchmarkThroughputUnit.PassesPerSecond => 1,
            BenchmarkThroughputUnit.AudioSecondsPerSecond => PositiveUnit(audioSecondsPerPass),
            BenchmarkThroughputUnit.ItemsPerSecond => PositiveUnit(itemsPerPass),
            _ => throw new ArgumentOutOfRangeException(nameof(unit))
        };
        BenchmarkLatencyStatistics? statistics = sampling.PolicyCompleted && sampling.Failure is null
            ? BenchmarkLatencyCalculator.Calculate(sampling.LatencySamplesMilliseconds)
            : null;
        double? passesPerSecond = statistics is not null && sampling.MeasuredWallTimeMilliseconds > 0
            ? Finite(statistics.SampleCount / (sampling.MeasuredWallTimeMilliseconds / 1000))
            : null;
        double? throughput = passesPerSecond.HasValue ? Finite(passesPerSecond.Value * unitsPerPass) : null;
        double? realTimeFactor = statistics is not null && audioSecondsPerPass is > 0
            ? Finite(statistics.MeanMilliseconds / (audioSecondsPerPass.Value * 1000))
            : null;

        return new BenchmarkMeasurements(
            sessionInitializationMilliseconds, sampling.WarmupMilliseconds,
            statistics?.MeanMilliseconds, statistics?.MinMilliseconds, statistics?.MaxMilliseconds,
            audioSecondsPerPass, realTimeFactor)
        {
            FirstRunMilliseconds = sampling.FirstRunMilliseconds,
            WarmupRunCount = sampling.WarmupRunCount,
            WarmLatencyP50Milliseconds = statistics?.P50Milliseconds,
            WarmLatencyP90Milliseconds = statistics?.P90Milliseconds,
            WarmLatencyP95Milliseconds = statistics?.P95Milliseconds,
            WarmLatencyP99Milliseconds = statistics?.P99Milliseconds,
            SampleCount = sampling.LatencySamplesMilliseconds.Count,
            MeasuredWallTimeMilliseconds = sampling.MeasuredWallTimeMilliseconds,
            PassesPerSecond = passesPerSecond,
            ThroughputUnitsPerSecond = throughput,
            ThroughputUnit = unit,
            LatencySamplesMilliseconds = sampling.LatencySamplesMilliseconds
        };
    }

    private static double PositiveUnit(double? value)
    {
        if (value is null || !double.IsFinite(value.Value) || value <= 0)
        {
            throw new ArgumentException("The throughput unit requires a finite, positive amount of work per pass.");
        }

        return value.Value;
    }

    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
}
