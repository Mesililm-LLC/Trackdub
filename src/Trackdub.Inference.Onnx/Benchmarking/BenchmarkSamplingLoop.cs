using Trackdub.Domain.Benchmarking;

namespace Trackdub.Inference.Onnx.Benchmarking;

internal sealed record BenchmarkSamplingResult(
    double? FirstRunMilliseconds,
    int WarmupRunCount,
    double WarmupMilliseconds,
    IReadOnlyList<double> LatencySamplesMilliseconds,
    double MeasuredWallTimeMilliseconds,
    bool PolicyCompleted,
    Exception? Failure);

/// <summary>Separates startup, additional warmup, and count-plus-time inference sampling.</summary>
internal static class BenchmarkSamplingLoop
{
    internal static BenchmarkSamplingResult Run(
        Action<CancellationToken> pass,
        int minimumSamples,
        BenchmarkExecutionSettings settings,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default,
        Action<BenchmarkSamplingPhase>? phaseObserver = null)
    {
        ArgumentNullException.ThrowIfNull(pass);
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate(minimumSamples);
        cancellationToken.ThrowIfCancellationRequested();
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        var samples = new List<double>(Math.Min(minimumSamples, 4096));
        double? firstRunMilliseconds = null;
        int warmupRuns = 0;
        double warmupMilliseconds = 0;
        double measuredWallMilliseconds = 0;
        long? warmupStart = null;
        long? measurementStart = null;

        try
        {
            firstRunMilliseconds = MeasurePass();
            phaseObserver?.Invoke(BenchmarkSamplingPhase.FirstRun);

            warmupStart = clock.GetTimestamp();
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                pass(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                warmupRuns++;
                warmupMilliseconds = Elapsed(warmupStart.Value);
            }
            while (warmupRuns < settings.MinimumWarmupRuns
                || warmupMilliseconds < settings.MinimumWarmupMilliseconds);
            phaseObserver?.Invoke(BenchmarkSamplingPhase.Warmup);

            measurementStart = clock.GetTimestamp();
            do
            {
                samples.Add(MeasurePass());
                measuredWallMilliseconds = Elapsed(measurementStart.Value);
            }
            while (samples.Count < minimumSamples
                || measuredWallMilliseconds < settings.MinimumMeasurementMilliseconds);
            phaseObserver?.Invoke(BenchmarkSamplingPhase.Measurement);
            cancellationToken.ThrowIfCancellationRequested();

            return Result(policyCompleted: true, failure: null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            if (measurementStart.HasValue)
            {
                measuredWallMilliseconds = Elapsed(measurementStart.Value);
            }
            else if (warmupStart.HasValue)
            {
                warmupMilliseconds = Elapsed(warmupStart.Value);
            }

            return Result(policyCompleted: false, failure: exception);
        }

        double MeasurePass()
        {
            cancellationToken.ThrowIfCancellationRequested();
            long start = clock.GetTimestamp();
            pass(cancellationToken);
            double duration = Elapsed(start);
            cancellationToken.ThrowIfCancellationRequested();
            return duration;
        }

        double Elapsed(long start) => clock.GetElapsedTime(start, clock.GetTimestamp()).TotalMilliseconds;

        BenchmarkSamplingResult Result(bool policyCompleted, Exception? failure) =>
            new(firstRunMilliseconds, warmupRuns, warmupMilliseconds, samples.ToArray(),
                measuredWallMilliseconds, policyCompleted, failure);
    }
}
