using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Diagnostics;

namespace Trackdub.Benchmarks.Tests;

public sealed class BenchmarkPhaseCaptureTests
{
    [Fact]
    public async Task CaptureScopesPhasesAndRestoresAmbientContext()
    {
        var first = new BenchmarkPhaseCapture();
        var second = new BenchmarkPhaseCapture();
        using (BenchmarkPhaseCapture.Activate(first))
        {
            using (BenchmarkPhaseCapture.Start("inference"))
                await Task.Delay(2);
            using (BenchmarkPhaseCapture.Activate(second))
            using (BenchmarkPhaseCapture.Start("session-create"))
                await Task.Delay(2);
            using (BenchmarkPhaseCapture.Start("inference"))
                await Task.Delay(2);
        }
        using (BenchmarkPhaseCapture.Start("unscoped"))
            await Task.Delay(1);

        Assert.True(first.SnapshotMilliseconds()["phase:inference"] > 0);
        Assert.DoesNotContain("phase:session-create", first.SnapshotMilliseconds().Keys);
        Assert.True(second.SnapshotMilliseconds()["phase:session-create"] > 0);
        Assert.DoesNotContain("phase:unscoped", first.SnapshotMilliseconds().Keys);
    }

    [Fact]
    public async Task Start_mirrors_spans_into_the_ambient_run_capture()
    {
        // Run evidence reuses the same phase names as the benchmark harness, so a phase
        // span recorded with no benchmark capture must still land on the active run capture.
        var runCapture = new ColdStartCapture();
        using (ColdStartCapture.Activate(runCapture))
        {
            using (BenchmarkPhaseCapture.Start("session-create"))
                await Task.Delay(2);
            using (BenchmarkPhaseCapture.Start("session-create"))
                await Task.Delay(2);
            using (BenchmarkPhaseCapture.Start("provider-bootstrap"))
                await Task.Delay(1);
        }

        var totals = runCapture.BuildTiming().PhaseTotals;
        Assert.Equal(2, totals["session-create"].Count);
        Assert.True(totals["session-create"].TotalMs > 0);
        Assert.Equal(1, totals["provider-bootstrap"].Count);
        // Aggregates are ordered by total elapsed descending.
        Assert.Equal("session-create", totals.Keys.First());
    }

    [Fact]
    public async Task Start_feeds_both_captures_when_both_are_active()
    {
        var benchmark = new BenchmarkPhaseCapture();
        var runCapture = new ColdStartCapture();
        using (BenchmarkPhaseCapture.Activate(benchmark))
        using (ColdStartCapture.Activate(runCapture))
        using (BenchmarkPhaseCapture.Start("onnx-inference"))
            await Task.Delay(2);

        Assert.True(benchmark.SnapshotMilliseconds()["phase:onnx-inference"] > 0);
        Assert.True(runCapture.BuildTiming().PhaseTotals["onnx-inference"].TotalMs > 0);
    }

    [Fact]
    public void Start_with_no_active_capture_records_nothing_and_does_not_throw()
    {
        using (BenchmarkPhaseCapture.Start("session-create"))
        {
        }
    }

    [Fact]
    public void CountersAndMaxima_AreAmbientScopedAndReturnRawNames()
    {
        var first = new BenchmarkPhaseCapture();
        var second = new BenchmarkPhaseCapture();

        using (BenchmarkPhaseCapture.Activate(first))
        {
            BenchmarkPhaseCapture.Increment("poolHit");
            BenchmarkPhaseCapture.Increment("poolHit", 2);
            BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 3);
            BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 1); // lower must not overwrite
            using (BenchmarkPhaseCapture.Activate(second))
            {
                BenchmarkPhaseCapture.Increment("poolHit", 10);
                BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 7);
            }
        }

        // Outside any ambient scope these are no-ops.
        BenchmarkPhaseCapture.Increment("poolHit");
        BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 99);

        Assert.Equal(3, first.SnapshotCounters()["poolHit"]);
        Assert.Equal(3, first.SnapshotMaxima()["admissionWaiters"]);
        Assert.Equal(10, second.SnapshotCounters()["poolHit"]);
        Assert.Equal(7, second.SnapshotMaxima()["admissionWaiters"]);
        Assert.DoesNotContain("poolHit", first.SnapshotMilliseconds().Keys);
    }

    [Fact]
    public void CountersAndMaxima_AreConcurrentSafe()
    {
        var capture = new BenchmarkPhaseCapture();
        const int tasks = 8;
        const int iterations = 500;

        using (BenchmarkPhaseCapture.Activate(capture))
        {
            Parallel.For(0, tasks, _ =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    BenchmarkPhaseCapture.Increment("poolHit");
                    BenchmarkPhaseCapture.ObserveMaximum("pendingReservationMb", i);
                }
            });
        }

        Assert.Equal(tasks * iterations, capture.SnapshotCounters()["poolHit"]);
        Assert.Equal(iterations - 1, capture.SnapshotMaxima()["pendingReservationMb"]);
    }

    [Fact]
    public void CountersAndMaxima_ValidateArgumentsEvenWithoutAmbientCapture()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkPhaseCapture.Increment(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkPhaseCapture.Increment("x", -1));
        Assert.Throws<ArgumentException>(() => BenchmarkPhaseCapture.ObserveMaximum("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkPhaseCapture.ObserveMaximum("x", -5));
    }
}
