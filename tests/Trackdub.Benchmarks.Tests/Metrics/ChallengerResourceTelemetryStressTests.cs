using System.Diagnostics;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests.Metrics;

[CollectionDefinition("SerialStressTests", DisableParallelization = true)]
public sealed class SerialStressTestsDefinition;

/// <summary>
/// Adversarial challenger empirical stress test harness for Milestone 2:
/// - 64-bit integer overflow bounds (e.g. 50GB, 100TB, long.MaxValue)
/// - Negative working set drops (memory trimming, native release)
/// - Rapid GC count increments and boundary checks
/// - Rapid multi-threaded CaptureProcess invocation (handle leak & deadlock check)
/// - Multi-threaded StageTimingCollector event reporting concurrency with reader contention
/// </summary>
[Collection("SerialStressTests")]
public sealed class ChallengerResourceTelemetryStressTests
{
    // =========================================================================
    // 1. 64-Bit Integer Overflow Bounds & Extreme Memory Values
    // =========================================================================

    [Theory]
    [InlineData(50L * 1024 * 1024 * 1024, 75L * 1024 * 1024 * 1024, 25L * 1024 * 1024 * 1024)] // 50 GB -> 75 GB = +25 GB
    [InlineData(128L * 1024 * 1024 * 1024, 256L * 1024 * 1024 * 1024, 128L * 1024 * 1024 * 1024)] // 128 GB -> 256 GB = +128 GB
    [InlineData(1024L * 1024 * 1024 * 1024, 2048L * 1024 * 1024 * 1024, 1024L * 1024 * 1024 * 1024)] // 1 TB -> 2 TB = +1 TB
    [InlineData(100L * 1024 * 1024 * 1024 * 1024, 150L * 1024 * 1024 * 1024 * 1024, 50L * 1024 * 1024 * 1024 * 1024)] // 100 TB -> 150 TB = +50 TB
    public void CalculateDelta_Extreme64BitMemoryValues_ProducesExactDeltasWithoutOverflow(
        long startBytes,
        long endBytes,
        long expectedDelta)
    {
        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: startBytes,
            PeakWorkingSetBytes: startBytes,
            ManagedAllocatedBytes: startBytes / 2,
            Gen0Collections: 100,
            Gen1Collections: 20,
            Gen2Collections: 5);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: endBytes,
            PeakWorkingSetBytes: endBytes + (10L * 1024 * 1024 * 1024), // +10 GB peak
            ManagedAllocatedBytes: (startBytes / 2) + expectedDelta,
            Gen0Collections: 150,
            Gen1Collections: 30,
            Gen2Collections: 8);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(expectedDelta, delta.WorkingSetDeltaBytes);
        Assert.Equal(end.WorkingSetBytes, delta.PeakWorkingSetBytes);
        Assert.Equal(expectedDelta, delta.ManagedAllocatedBytes);
        Assert.Equal(50, delta.Gen0Collections);
        Assert.Equal(10, delta.Gen1Collections);
        Assert.Equal(3, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_NearInt64MaxValue_DoesNotOverflow()
    {
        long almostMax = long.MaxValue - 1_000_000L;
        long max = long.MaxValue;

        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: almostMax,
            PeakWorkingSetBytes: almostMax,
            ManagedAllocatedBytes: almostMax,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: max,
            PeakWorkingSetBytes: max,
            ManagedAllocatedBytes: max,
            Gen0Collections: 10,
            Gen1Collections: 5,
            Gen2Collections: 2);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(1_000_000L, delta.WorkingSetDeltaBytes);
        Assert.Equal(max, delta.PeakWorkingSetBytes);
        Assert.Equal(1_000_000L, delta.ManagedAllocatedBytes);
    }

    [Fact]
    public void CalculateDelta_Int64MaxValueFromZero_YieldsMaxValue()
    {
        var start = ResourceTelemetrySnapshot.Empty;
        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: long.MaxValue,
            PeakWorkingSetBytes: long.MaxValue,
            ManagedAllocatedBytes: long.MaxValue,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(long.MaxValue, delta.WorkingSetDeltaBytes);
        Assert.Equal(long.MaxValue, delta.PeakWorkingSetBytes);
        Assert.Equal(long.MaxValue, delta.ManagedAllocatedBytes);
    }

    // =========================================================================
    // 2. Negative Working Set Drops (OS Trimming & Memory Release)
    // =========================================================================

    [Theory]
    [InlineData(64L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024, -62L * 1024 * 1024 * 1024)] // 64 GB -> 2 GB = -62 GB
    [InlineData(50L * 1024 * 1024 * 1024, 100L * 1024 * 1024, -((50L * 1024 * 1024 * 1024) - (100L * 1024 * 1024)))] // 50 GB -> 100 MB
    [InlineData(500_000_000L, 50_000_000L, -450_000_000L)] // 500 MB -> 50 MB
    public void CalculateDelta_NegativeWorkingSetDrops_CorrectlyCapturesNegativeDeltaAndPreservesPeak(
        long startWorkingSet,
        long endWorkingSet,
        long expectedDelta)
    {
        long peak = startWorkingSet + (1024 * 1024);
        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: startWorkingSet,
            PeakWorkingSetBytes: peak,
            ManagedAllocatedBytes: 10_000_000,
            Gen0Collections: 5,
            Gen1Collections: 2,
            Gen2Collections: 1);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: endWorkingSet,
            PeakWorkingSetBytes: peak,
            ManagedAllocatedBytes: 12_000_000,
            Gen0Collections: 6,
            Gen1Collections: 3,
            Gen2Collections: 1);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(expectedDelta, delta.WorkingSetDeltaBytes);
        Assert.True(delta.WorkingSetDeltaBytes < 0, "Working set delta must be negative on memory trim/drop.");
        Assert.Equal(startWorkingSet, delta.PeakWorkingSetBytes);
        Assert.Equal(2_000_000, delta.ManagedAllocatedBytes);
        Assert.Equal(1, delta.Gen0Collections);
        Assert.Equal(1, delta.Gen1Collections);
        Assert.Equal(0, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_WorkingSetDropsBetweenSnapshots_PeakUsesHigherBoundary()
    {
        // Working set was trimmed between the two snapshots. PeakWorkingSetBytes on the
        // snapshots themselves (the OS-reported process-lifetime peak) is intentionally not
        // used here — CalculateDelta's PeakWorkingSetBytes is the higher of the two
        // snapshots' WorkingSetBytes, i.e. start's, since it dropped by the end.
        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 40_000_000_000L, // 40 GB
            PeakWorkingSetBytes: 45_000_000_000L, // 45 GB
            ManagedAllocatedBytes: 10_000_000,
            Gen0Collections: 0,
            Gen1Collections: 0,
            Gen2Collections: 0);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 10_000_000_000L, // 10 GB (trimmed)
            PeakWorkingSetBytes: 60_000_000_000L, // 60 GB (spiked during run)
            ManagedAllocatedBytes: 20_000_000,
            Gen0Collections: 1,
            Gen1Collections: 0,
            Gen2Collections: 0);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(-30_000_000_000L, delta.WorkingSetDeltaBytes);
        Assert.Equal(40_000_000_000L, delta.PeakWorkingSetBytes);
    }

    // =========================================================================
    // 3. Rapid GC Count Increments and 32-Bit Integer Bounds
    // =========================================================================

    [Fact]
    public void CalculateDelta_RapidGcIncrements_HandlesMillionCollections()
    {
        const int million = 1_000_000;
        var start = new ResourceTelemetrySnapshot(100, 100, 100, 50, 10, 2);
        var end = new ResourceTelemetrySnapshot(100, 100, 100, 50 + million, 10 + (million / 10), 2 + (million / 100));

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(million, delta.Gen0Collections);
        Assert.Equal(million / 10, delta.Gen1Collections);
        Assert.Equal(million / 100, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_Int32MaxValueGcCounts_DoesNotOverflow()
    {
        var start = new ResourceTelemetrySnapshot(100, 100, 100, int.MaxValue - 500, int.MaxValue - 100, int.MaxValue - 10);
        var end = new ResourceTelemetrySnapshot(100, 100, 100, int.MaxValue, int.MaxValue, int.MaxValue);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(500, delta.Gen0Collections);
        Assert.Equal(100, delta.Gen1Collections);
        Assert.Equal(10, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_DecreasedGcCount_ClampsToZero()
    {
        var start = new ResourceTelemetrySnapshot(100, 100, 100, 1000, 200, 50);
        var end = new ResourceTelemetrySnapshot(100, 100, 100, 500, 100, 10);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(0, delta.Gen0Collections);
        Assert.Equal(0, delta.Gen1Collections);
        Assert.Equal(0, delta.Gen2Collections);
    }

    // =========================================================================
    // 4. CaptureProcess() Multi-Threaded Stress & Handle Leak Verification
    // =========================================================================

    [Trait("Category", "Slow")]
    [Fact]
    public void CaptureProcess_RapidMultiThreadedInvocation_DoesNotLeakHandlesOrDeadlock()
    {
        const int threadCount = 20;
        const int iterationsPerThread = 25;
        const int totalInvocations = threadCount * iterationsPerThread; // 500 invocations

        using var procBefore = Process.GetCurrentProcess();
        procBefore.Refresh();
        int initialHandleCount = procBefore.HandleCount;

        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var snapshots = new ResourceTelemetrySnapshot[totalInvocations];

        using var barrier = new Barrier(threadCount);

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadIdx =>
        {
            try
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(10));
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    int index = (threadIdx * iterationsPerThread) + i;
                    snapshots[index] = ResourceTelemetry.CaptureProcess();
                }
            }
            catch (OperationCanceledException ex)
            {
                exceptions.Add(ex);
            }
            catch (InvalidOperationException ex)
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);

        // Verify validity of all captured snapshots
        for (int i = 0; i < totalInvocations; i++)
        {
            ResourceTelemetrySnapshot s = snapshots[i];
            Assert.NotNull(s);
            Assert.True(s.WorkingSetBytes > 0, $"WorkingSetBytes should be > 0 at index {i}");
            Assert.True(s.PeakWorkingSetBytes >= s.WorkingSetBytes, $"PeakWorkingSetBytes should be >= WorkingSetBytes at index {i}");
            Assert.True(s.ManagedAllocatedBytes > 0, $"ManagedAllocatedBytes should be > 0 at index {i}");
            Assert.True(s.Gen0Collections >= s.Gen1Collections, $"Gen0 >= Gen1 at index {i}");
            Assert.True(s.Gen1Collections >= s.Gen2Collections, $"Gen1 >= Gen2 at index {i}");
        }

        // Wait for pending finalizers to settle before handle count check
        GC.WaitForPendingFinalizers();

        using var procAfter = Process.GetCurrentProcess();
        procAfter.Refresh();
        int finalHandleCount = procAfter.HandleCount;

        // Process.GetCurrentProcess().Dispose() should clean up any duplicated process handles.
        // Tolerating a small runtime threadpool/CLR jitter delta of 150 handles.
        int handleDelta = Math.Abs(finalHandleCount - initialHandleCount);
        Assert.True(handleDelta < 150,
            $"Potential handle leak: initial handles = {initialHandleCount}, final handles = {finalHandleCount}, delta = {handleDelta}");
    }

    // =========================================================================
    // 5. StageTimingCollector Multi-Threaded Event Reporting & Reader Contention
    // =========================================================================

    [Fact]
    public async Task StageTimingCollector_50ConcurrentTasksWithReaders_ThreadSafeAndConsistent()
    {
        long runStart = Stopwatch.GetTimestamp();
        var collector = new StageTimingCollector(runStart, FixedResourceSnapshot.Provide);

        const int workerTaskCount = 50;
        const int iterationsPerTask = 20;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var writeExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var readExceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 5 continuous reader tasks that poll dictionaries during active writes
        var readerTasks = Enumerable.Range(0, 5).Select(readerIdx => Task.Run(() =>
        {
            try
            {
                while (!cts.Token.IsCancellationRequested)
                {
                    var d = collector.GetAllMemoryDeltas();
                    var ms = collector.GetAllMemorySamples();
                    var s = collector.GetAllSamples();
                    var ms0 = collector.GetMilliseconds("stage-0");
                    var md0 = collector.GetMemoryDelta("stage-0");
                    var sm0 = collector.GetMemorySamples("stage-0");
                    var sn0 = collector.GetStartMemorySnapshot("stage-0");
                    Thread.Yield();
                }
            }
            catch (Exception ex) when (!cts.Token.IsCancellationRequested)
            {
                readExceptions.Add(ex);
            }
        })).ToArray();

        // 50 writer tasks:
        // Tasks 0..24 write to unique stages (stage-0 to stage-24)
        // Tasks 25..49 write concurrently to a single shared stage ("shared-hot-stage")
        var writerTasks = Enumerable.Range(0, workerTaskCount).Select(taskId => Task.Run(() =>
        {
            try
            {
                string stage = taskId < 25 ? $"stage-{taskId}" : "shared-hot-stage";
                for (int iter = 0; iter < iterationsPerTask; iter++)
                {
                    collector.Report(new PipelineProgressEvent(
                        StageName: stage,
                        EventKind: PipelineProgressEventKind.Started));

                    collector.Report(new PipelineProgressEvent(
                        StageName: stage,
                        EventKind: PipelineProgressEventKind.Progress,
                        PercentComplete: 50.0));

                    collector.Report(new PipelineProgressEvent(
                        StageName: stage,
                        EventKind: PipelineProgressEventKind.Completed));
                }
            }
            catch (OperationCanceledException ex) when (cts.Token.IsCancellationRequested)
            {
                writeExceptions.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        cts.Cancel();
        await Task.WhenAll(readerTasks);

        Assert.Empty(writeExceptions);
        Assert.Empty(readExceptions);

        // Verify data consistency
        // 25 unique stages + 1 shared stage = 26 stages total
        var allDeltas = collector.GetAllMemoryDeltas();
        Assert.Equal(26, allDeltas.Count);

        var allSamples = collector.GetAllMemorySamples();
        Assert.Equal(26, allSamples.Count);

        // Verify unique stages each have iterationsPerTask samples
        for (int i = 0; i < 25; i++)
        {
            string stage = $"stage-{i}";
            Assert.True(allDeltas.ContainsKey(stage));
            Assert.Equal(iterationsPerTask, allSamples[stage].Count);
        }

        // Verify shared stage has 25 * iterationsPerTask samples
        Assert.True(allDeltas.ContainsKey("shared-hot-stage"));
        Assert.Equal(25 * iterationsPerTask, allSamples["shared-hot-stage"].Count);
    }

    // =========================================================================
    // 6. StageTimingCollector Mismatched StageKey and StageName
    // =========================================================================

    [Fact]
    public void StageTimingCollector_DistinctStageKeyAndStageName_PopulatesBothKeysEquivalently()
    {
        var startSnapshot = new ResourceTelemetrySnapshot(100_000_000, 110_000_000, 20_000_000, 2, 1, 0);
        var endSnapshot = new ResourceTelemetrySnapshot(150_000_000, 160_000_000, 45_000_000, 5, 2, 1);

        int callCount = 0;
        ResourceTelemetrySnapshot MockProvider() => ++callCount == 1 ? startSnapshot : endSnapshot;

        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), MockProvider);

        collector.Report(new PipelineProgressEvent(
            StageName: "Speech Recognition Engine",
            StageKey: "asr_stage",
            EventKind: PipelineProgressEventKind.Started));

        collector.Report(new PipelineProgressEvent(
            StageName: "Speech Recognition Engine",
            StageKey: "asr_stage",
            EventKind: PipelineProgressEventKind.Completed));

        ResourceTelemetryDelta? deltaByKey = collector.GetMemoryDelta("asr_stage");
        ResourceTelemetryDelta? deltaByName = collector.GetMemoryDelta("Speech Recognition Engine");

        Assert.NotNull(deltaByKey);
        Assert.NotNull(deltaByName);
        Assert.Equal(deltaByKey, deltaByName);
        Assert.Equal(50_000_000, deltaByKey.WorkingSetDeltaBytes);
        Assert.Equal(25_000_000, deltaByKey.ManagedAllocatedBytes);

        var allDeltas = collector.GetAllMemoryDeltas();
        Assert.True(allDeltas.ContainsKey("asr_stage"));
        Assert.True(allDeltas.ContainsKey("Speech Recognition Engine"));
    }

    // =========================================================================
    // 7. StageTimingCollector Edge Cases: Unstarted, Skipped, and Repeated Events
    // =========================================================================

    [Fact]
    public void StageTimingCollector_TerminalEventWithoutPriorStart_DoesNotCrashOrRecordGarbage()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(
            StageName: "ghost-stage",
            EventKind: PipelineProgressEventKind.Completed));

        Assert.Null(collector.GetMemoryDelta("ghost-stage"));
        Assert.Null(collector.GetMilliseconds("ghost-stage"));
        Assert.Empty(collector.GetMemorySamples("ghost-stage"));
    }

    [Fact]
    public void StageTimingCollector_NullPipelineProgressEvent_ThrowsArgumentNullException()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        Assert.Throws<ArgumentNullException>(() => collector.Report(null!));
    }

    // =========================================================================
    // 8. Mixed Event Streams (Failed, Skipped, Retried, and Out-of-Order)
    // =========================================================================

    [Fact]
    public void StageTimingCollector_MixedEventStreams_HandlesRetryAndTerminalKindsGracefully()
    {
        int snapshotCalls = 0;
        ResourceTelemetrySnapshot Provider()
        {
            int c = Interlocked.Increment(ref snapshotCalls);
            return new ResourceTelemetrySnapshot(100 + c, 200 + c, 50 + c, c, 0, 0);
        }

        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), Provider);

        // Stage 1: Started -> Started (retry) -> Completed
        collector.Report(new PipelineProgressEvent(StageName: "retry-stage", EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: "retry-stage", EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: "retry-stage", EventKind: PipelineProgressEventKind.Completed));

        Assert.NotNull(collector.GetMemoryDelta("retry-stage"));
        Assert.Single(collector.GetMemorySamples("retry-stage"));

        // Stage 2: Started -> Failed
        collector.Report(new PipelineProgressEvent(StageName: "fail-stage", EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: "fail-stage", EventKind: PipelineProgressEventKind.Failed));

        Assert.NotNull(collector.GetMemoryDelta("fail-stage"));
        Assert.Single(collector.GetMemorySamples("fail-stage"));

        // Stage 3: Started -> Skipped
        collector.Report(new PipelineProgressEvent(StageName: "skip-stage", EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: "skip-stage", EventKind: PipelineProgressEventKind.Skipped));

        Assert.NotNull(collector.GetMemoryDelta("skip-stage"));
        Assert.Single(collector.GetMemorySamples("skip-stage"));
    }

    // =========================================================================
    // 9. Defensive Copying Verification
    // =========================================================================

    [Fact]
    public void StageTimingCollector_DefensiveCopies_ExternalMutationsDoNotCorruptInternalState()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(StageName: "defense-stage", EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: "defense-stage", EventKind: PipelineProgressEventKind.Completed));

        var samples = collector.GetMemorySamples("defense-stage");
        var allSamples = collector.GetAllMemorySamples();
        var allDeltas = collector.GetAllMemoryDeltas();

        // Ensure returned dictionaries and collections are independent instances
        Assert.Single(samples);
        Assert.Single(allSamples);
        Assert.Single(allDeltas);

        if (allDeltas is Dictionary<string, ResourceTelemetryDelta> mutableDeltas)
        {
            mutableDeltas.Clear();
            Assert.NotNull(collector.GetMemoryDelta("defense-stage"));
            Assert.Single(collector.GetAllMemoryDeltas());
        }
    }

    // =========================================================================
    // 10. Multi-Run Memory Aggregation Math Simulation
    // =========================================================================

    [Fact]
    public void MultiRunMemoryAggregation_Simulated10Runs_ProducesBoundedMedianAndMax()
    {
        var random = new Random(42);
        var samples = new List<ResourceTelemetryDelta>();

        for (int i = 0; i < 10; i++)
        {
            long wsDelta = random.Next(-100_000_000, 200_000_000); // Can be negative
            long peak = 1_000_000_000L + random.Next(0, 500_000_000);
            long alloc = (long)random.Next(50_000_000, 300_000_000);
            int g0 = random.Next(1, 20);
            int g1 = random.Next(0, 5);
            int g2 = random.Next(0, 2);

            samples.Add(new ResourceTelemetryDelta(wsDelta, peak, alloc, g0, g1, g2));
        }

        var sortedAlloc = samples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
        long medianAlloc = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
        long peakWs = samples.Max(s => s.PeakWorkingSetBytes);
        int gen0 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen0Collections).OrderBy(x => x).ToArray(), 0.5));
        int gen1 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen1Collections).OrderBy(x => x).ToArray(), 0.5));
        int gen2 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen2Collections).OrderBy(x => x).ToArray(), 0.5));

        Assert.InRange(medianAlloc, samples.Min(s => s.ManagedAllocatedBytes), samples.Max(s => s.ManagedAllocatedBytes));
        Assert.Equal(samples.Max(s => s.PeakWorkingSetBytes), peakWs);
        Assert.InRange(gen0, samples.Min(s => s.Gen0Collections), samples.Max(s => s.Gen0Collections));
        Assert.InRange(gen1, samples.Min(s => s.Gen1Collections), samples.Max(s => s.Gen1Collections));
        Assert.InRange(gen2, samples.Min(s => s.Gen2Collections), samples.Max(s => s.Gen2Collections));
    }
}
