using System.Diagnostics;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class StageResourceTelemetryTests
{
    [Fact]
    public void StageTimingCollector_RecordsMemoryDelta_OnStageCompletion()
    {
        var startSnapshot = new ResourceTelemetrySnapshot(100_000_000, 110_000_000, 20_000_000, 5, 2, 0);
        var endSnapshot = new ResourceTelemetrySnapshot(140_000_000, 150_000_000, 55_000_000, 9, 4, 1);

        int callCount = 0;
        ResourceTelemetrySnapshot MockProvider() => ++callCount == 1 ? startSnapshot : endSnapshot;

        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), MockProvider);

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Asr,
            EventKind: PipelineProgressEventKind.Started));

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Asr,
            EventKind: PipelineProgressEventKind.Completed));

        ResourceTelemetryDelta? delta = collector.GetMemoryDelta(StageNames.Asr);

        Assert.NotNull(delta);
        Assert.Equal(40_000_000, delta.WorkingSetDeltaBytes);
        Assert.Equal(140_000_000, delta.PeakWorkingSetBytes);
        Assert.Equal(35_000_000, delta.ManagedAllocatedBytes);
        Assert.Equal(4, delta.Gen0Collections);
        Assert.Equal(2, delta.Gen1Collections);
        Assert.Equal(1, delta.Gen2Collections);
    }

    [Theory]
    [InlineData(PipelineProgressEventKind.Failed)]
    [InlineData(PipelineProgressEventKind.Skipped)]
    public void StageTimingCollector_TerminalEvents_RecordMemoryDelta(PipelineProgressEventKind terminalKind)
    {
        var startSnapshot = new ResourceTelemetrySnapshot(50_000_000, 50_000_000, 10_000_000, 0, 0, 0);
        var endSnapshot = new ResourceTelemetrySnapshot(60_000_000, 65_000_000, 15_000_000, 1, 0, 0);

        int callCount = 0;
        ResourceTelemetrySnapshot MockProvider() => ++callCount == 1 ? startSnapshot : endSnapshot;

        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), MockProvider);

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Separation,
            EventKind: PipelineProgressEventKind.Started));

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Separation,
            EventKind: terminalKind));

        ResourceTelemetryDelta? delta = collector.GetMemoryDelta(StageNames.Separation);

        Assert.NotNull(delta);
        Assert.Equal(5_000_000, delta.ManagedAllocatedBytes);
        Assert.Equal(60_000_000, delta.PeakWorkingSetBytes);
    }

    [Fact]
    public void StageTimingCollector_UnstartedStage_ReturnsNullMemoryDelta()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        Assert.Null(collector.GetMemoryDelta("non-existent-stage"));
        Assert.Null(collector.GetStartMemorySnapshot("non-existent-stage"));
    }

    [Fact]
    public void StageTimingCollector_StartedWithoutCompletion_ReturnsNullMemoryDelta()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Tts,
            EventKind: PipelineProgressEventKind.Started));

        Assert.NotNull(collector.GetStartMemorySnapshot(StageNames.Tts));
        Assert.Null(collector.GetMemoryDelta(StageNames.Tts));
    }

    [Fact]
    public void StageTimingCollector_MemoryLookupIsCaseInsensitive()
    {
        var start = new ResourceTelemetrySnapshot(100, 100, 10, 0, 0, 0);
        var end = new ResourceTelemetrySnapshot(120, 120, 20, 1, 0, 0);

        int calls = 0;
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), () => ++calls == 1 ? start : end);

        collector.Report(new PipelineProgressEvent(StageName: "ASR", EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: "asr", EventKind: PipelineProgressEventKind.Completed));

        ResourceTelemetryDelta? deltaUpper = collector.GetMemoryDelta("ASR");
        ResourceTelemetryDelta? deltaLower = collector.GetMemoryDelta("asr");
        ResourceTelemetryDelta? deltaMixed = collector.GetMemoryDelta("Asr");

        Assert.NotNull(deltaUpper);
        Assert.Equal(deltaUpper, deltaLower);
        Assert.Equal(deltaUpper, deltaMixed);
    }

    [Fact]
    public void StageTimingCollector_AllFiveCanonicalStages_RecordDistinctMemoryDeltas()
    {
        string[] canonicalStages =
        [
            StageNames.AudioPreparation,
            StageNames.Separation,
            StageNames.Asr,
            StageNames.LipSync,
            StageNames.Tts,
        ];

        long counter = 0;
        ResourceTelemetrySnapshot SequentialProvider()
        {
            long val = Interlocked.Increment(ref counter) * 10_000_000;
            return new ResourceTelemetrySnapshot(val, val + 5_000_000, val, (int)counter, 0, 0);
        }

        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), SequentialProvider);

        foreach (string stage in canonicalStages)
        {
            collector.Report(new PipelineProgressEvent(StageName: stage, EventKind: PipelineProgressEventKind.Started));
            collector.Report(new PipelineProgressEvent(StageName: stage, EventKind: PipelineProgressEventKind.Completed));
        }

        var allDeltas = collector.GetAllMemoryDeltas();
        Assert.Equal(5, allDeltas.Count);

        foreach (string stage in canonicalStages)
        {
            ResourceTelemetryDelta? delta = collector.GetMemoryDelta(stage);
            Assert.NotNull(delta);
            Assert.True(delta.ManagedAllocatedBytes > 0, $"Stage '{stage}' should have positive managed allocated bytes.");
            Assert.True(delta.PeakWorkingSetBytes > 0, $"Stage '{stage}' should have positive peak working set.");
        }
    }

    [Fact]
    public void StageTimingCollector_MultiRunSamples_AccumulatesMemorySamples()
    {
        int step = 0;
        ResourceTelemetrySnapshot Provider() =>
            new(100, 100, Interlocked.Increment(ref step) * 1_000_000, 0, 0, 0);

        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), Provider);

        for (int i = 0; i < 3; i++)
        {
            collector.Report(new PipelineProgressEvent(StageName: StageNames.LipSync, EventKind: PipelineProgressEventKind.Started));
            collector.Report(new PipelineProgressEvent(StageName: StageNames.LipSync, EventKind: PipelineProgressEventKind.Completed));
        }

        IReadOnlyList<ResourceTelemetryDelta> samples = collector.GetMemorySamples(StageNames.LipSync);
        Assert.Equal(3, samples.Count);

        var allSamples = collector.GetAllMemorySamples();
        Assert.True(allSamples.ContainsKey(StageNames.LipSync));
        Assert.Equal(3, allSamples[StageNames.LipSync].Count);
    }

    [Fact]
    public void StageTimingCollector_ThreadSafety_ConcurrentMemoryReportingHandledSafely()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), FixedResourceSnapshot.Provide);
        const int taskCount = 8;
        const int iterations = 25;

        Parallel.For(0, taskCount, t =>
        {
            string stage = $"concurrent-stage-{t}";
            for (int i = 0; i < iterations; i++)
            {
                collector.Report(new PipelineProgressEvent(StageName: stage, EventKind: PipelineProgressEventKind.Started));
                collector.Report(new PipelineProgressEvent(StageName: stage, EventKind: PipelineProgressEventKind.Completed));
            }
        });

        var allDeltas = collector.GetAllMemoryDeltas();
        Assert.Equal(taskCount, allDeltas.Count);

        var allSamples = collector.GetAllMemorySamples();
        Assert.Equal(taskCount, allSamples.Count);
        foreach ((string _, IReadOnlyList<ResourceTelemetryDelta> samples) in allSamples)
        {
            Assert.Equal(iterations, samples.Count);
        }
    }
}
