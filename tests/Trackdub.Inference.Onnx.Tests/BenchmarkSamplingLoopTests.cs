using Trackdub.Domain.Benchmarking;
using Trackdub.Inference.Onnx.Benchmarking;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class BenchmarkSamplingLoopTests
{
    [Fact]
    public void Run_ExcludesFirstRunAndAdditionalWarmupFromMeasuredSamples()
    {
        var clock = new ScriptedClock();
        int passes = 0;

        BenchmarkSamplingResult result = BenchmarkSamplingLoop.Run(
            _ =>
            {
                passes++;
                clock.Advance(5);
            },
            3, BenchmarkExecutionSettings.Default, clock, TestContext.Current.CancellationToken);

        Assert.True(result.PolicyCompleted);
        Assert.Null(result.Failure);
        Assert.Equal(5, result.FirstRunMilliseconds);
        Assert.Equal(40, result.WarmupRunCount);
        Assert.Equal(200, result.WarmupMilliseconds);
        Assert.Equal(new double[] { 5, 5, 5 }, result.LatencySamplesMilliseconds);
        Assert.Equal(15, result.MeasuredWallTimeMilliseconds);
        Assert.Equal(44, passes);
    }

    [Theory]
    [InlineData(3, 30, 6)]
    [InlineData(10, 30, 10)]
    public void Run_RequiresBothCountAndDurationFloors(int minimumSamples, int duration, int expectedCount)
    {
        var clock = new ScriptedClock();
        var settings = new BenchmarkExecutionSettings(
            MinimumWarmupMilliseconds: 0, MinimumMeasurementMilliseconds: duration);

        BenchmarkSamplingResult result = BenchmarkSamplingLoop.Run(
            _ => clock.Advance(5), minimumSamples, settings, clock, TestContext.Current.CancellationToken);

        Assert.True(result.PolicyCompleted);
        Assert.Equal(expectedCount, result.LatencySamplesMilliseconds.Count);
        Assert.Equal(expectedCount * 5, result.MeasuredWallTimeMilliseconds);
    }

    [Fact]
    public void Run_PhaseObserversDoNotContaminateTimingWindows()
    {
        var clock = new ScriptedClock();
        var observed = new List<BenchmarkSamplingPhase>();
        var settings = new BenchmarkExecutionSettings(MinimumWarmupMilliseconds: 0);

        BenchmarkSamplingResult result = BenchmarkSamplingLoop.Run(
            _ => clock.Advance(5), 1, settings, clock, TestContext.Current.CancellationToken,
            phase =>
            {
                observed.Add(phase);
                clock.Advance(1000);
            });

        Assert.Equal(
            new[] { BenchmarkSamplingPhase.FirstRun, BenchmarkSamplingPhase.Warmup, BenchmarkSamplingPhase.Measurement },
            observed);
        Assert.Equal(5, result.FirstRunMilliseconds);
        Assert.Equal(5, result.WarmupMilliseconds);
        Assert.Equal(5, result.MeasuredWallTimeMilliseconds);
    }

    [Fact]
    public void Run_FailedPassIsNotAddedAndPartialResultIsNotComplete()
    {
        var clock = new ScriptedClock();
        int passes = 0;
        var failure = new InvalidOperationException("scripted inference failure");
        var settings = new BenchmarkExecutionSettings(MinimumWarmupMilliseconds: 0);

        BenchmarkSamplingResult result = BenchmarkSamplingLoop.Run(
            _ =>
            {
                passes++;
                clock.Advance(5);
                if (passes == 4)
                {
                    throw failure;
                }
            },
            3, settings, clock, TestContext.Current.CancellationToken);

        Assert.False(result.PolicyCompleted);
        Assert.Same(failure, result.Failure);
        Assert.Equal(new double[] { 5 }, result.LatencySamplesMilliseconds);
    }

    [Fact]
    public void Run_CancellationStopsBeforeAnotherPass()
    {
        var clock = new ScriptedClock();
        using var cancellation = new CancellationTokenSource();
        int passes = 0;

        Assert.Throws<OperationCanceledException>(() => BenchmarkSamplingLoop.Run(
            _ =>
            {
                passes++;
                clock.Advance(5);
                cancellation.Cancel();
            },
            3, BenchmarkExecutionSettings.Default, clock, cancellation.Token));
        Assert.Equal(1, passes);
    }

    [Fact]
    public void Run_RejectsInvalidBudgetBeforeInvokingWork()
    {
        int passes = 0;
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkSamplingLoop.Run(
            _ => passes++, 0, BenchmarkExecutionSettings.Default,
            new ScriptedClock(), TestContext.Current.CancellationToken));
        Assert.Equal(0, passes);
    }

    private sealed class ScriptedClock : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _timestamp;
        public void Advance(int milliseconds) => _timestamp += milliseconds;
    }
}
