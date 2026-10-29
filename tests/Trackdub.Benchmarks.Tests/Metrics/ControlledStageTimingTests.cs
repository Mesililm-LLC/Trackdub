using System.Diagnostics;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class ControlledStageTimingTests
{
    [Fact]
    public void StageTimingCollector_RecordsAllFiveCanonicalStages()
    {
        long runStart = Stopwatch.GetTimestamp();
        var collector = new StageTimingCollector(runStart);

        string[] canonicalStages =
        [
            StageNames.AudioPreparation,
            StageNames.Separation,
            StageNames.Asr,
            StageNames.LipSync,
            StageNames.Tts,
        ];

        foreach (string stage in canonicalStages)
        {
            collector.Report(new PipelineProgressEvent(
                StageName: stage,
                EventKind: PipelineProgressEventKind.Started));

            Thread.Sleep(2);

            collector.Report(new PipelineProgressEvent(
                StageName: stage,
                EventKind: PipelineProgressEventKind.Completed));
        }

        foreach (string stage in canonicalStages)
        {
            double? duration = collector.GetMilliseconds(stage);
            double? completion = collector.GetCompletionMilliseconds(stage);

            Assert.NotNull(duration);
            Assert.True(duration > 0, $"Stage '{stage}' duration should be greater than 0.");
            Assert.NotNull(completion);
            Assert.True(completion > 0, $"Stage '{stage}' completion timestamp should be greater than 0.");
        }
    }

    [Fact]
    public void StageTimingCollector_LookupIsCaseInsensitive()
    {
        long runStart = Stopwatch.GetTimestamp();
        var collector = new StageTimingCollector(runStart);

        collector.Report(new PipelineProgressEvent(StageName: "ASR", EventKind: PipelineProgressEventKind.Started));
        Thread.Sleep(2);
        collector.Report(new PipelineProgressEvent(StageName: "asr", EventKind: PipelineProgressEventKind.Completed));

        Assert.NotNull(collector.GetMilliseconds("asr"));
        Assert.NotNull(collector.GetMilliseconds("Asr"));
        Assert.NotNull(collector.GetMilliseconds("ASR"));
        Assert.Equal(collector.GetMilliseconds("asr"), collector.GetMilliseconds("ASR"));
    }

    [Fact]
    public void StageTimingCollector_UnstartedStage_ReturnsNull()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        Assert.Null(collector.GetMilliseconds("non-existent-stage"));
        Assert.Null(collector.GetCompletionMilliseconds("non-existent-stage"));
    }

    [Fact]
    public void StageTimingCollector_StartedWithoutCompletion_ReturnsNull()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(StageName: StageNames.Asr, EventKind: PipelineProgressEventKind.Started));

        Assert.Null(collector.GetMilliseconds(StageNames.Asr));
        Assert.Null(collector.GetCompletionMilliseconds(StageNames.Asr));
    }

    [Theory]
    [InlineData(PipelineProgressEventKind.Failed)]
    [InlineData(PipelineProgressEventKind.Skipped)]
    public void StageTimingCollector_TerminalEventsRecordDuration(PipelineProgressEventKind terminalKind)
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(StageName: StageNames.Separation, EventKind: PipelineProgressEventKind.Started));
        Thread.Sleep(2);
        collector.Report(new PipelineProgressEvent(StageName: StageNames.Separation, EventKind: terminalKind));

        Assert.NotNull(collector.GetMilliseconds(StageNames.Separation));
        Assert.True(collector.GetMilliseconds(StageNames.Separation) > 0);
    }

    [Fact]
    public void StageTimingCollector_IgnoresIntermediateProgressEvents()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(StageName: StageNames.Tts, EventKind: PipelineProgressEventKind.Started));
        collector.Report(new PipelineProgressEvent(StageName: StageNames.Tts, EventKind: PipelineProgressEventKind.Progress, PercentComplete: 25.0));
        collector.Report(new PipelineProgressEvent(StageName: StageNames.Tts, EventKind: PipelineProgressEventKind.Progress, PercentComplete: 75.0));
        Thread.Sleep(2);
        collector.Report(new PipelineProgressEvent(StageName: StageNames.Tts, EventKind: PipelineProgressEventKind.Completed));

        Assert.NotNull(collector.GetMilliseconds(StageNames.Tts));
        Assert.True(collector.GetMilliseconds(StageNames.Tts) > 0);
    }

    [Fact]
    public void StageTimingCollector_MultipleRuns_RetainsAllSamples()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        for (int i = 0; i < 3; i++)
        {
            collector.Report(new PipelineProgressEvent(StageName: StageNames.Asr, EventKind: PipelineProgressEventKind.Started));
            Thread.Sleep(2);
            collector.Report(new PipelineProgressEvent(StageName: StageNames.Asr, EventKind: PipelineProgressEventKind.Completed));
        }

        IReadOnlyList<double> samples = collector.GetSamples(StageNames.Asr);
        Assert.Equal(3, samples.Count);
        Assert.All(samples, s => Assert.True(s > 0));

        var all = collector.GetAllSamples();
        Assert.True(all.ContainsKey(StageNames.Asr));
        Assert.Equal(3, all[StageNames.Asr].Count);
    }

    [Fact]
    public void StageTimingCollector_ThreadSafety_ConcurrentReportsHandledWithoutException()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp(), FixedResourceSnapshot.Provide);
        const int taskCount = 10;
        const int iterations = 50;

        Parallel.For(0, taskCount, t =>
        {
            string stage = $"stage-{t}";
            for (int i = 0; i < iterations; i++)
            {
                collector.Report(new PipelineProgressEvent(StageName: stage, EventKind: PipelineProgressEventKind.Started));
                collector.Report(new PipelineProgressEvent(StageName: stage, EventKind: PipelineProgressEventKind.Completed));
            }
        });

        var allSamples = collector.GetAllSamples();
        Assert.Equal(taskCount, allSamples.Count);
        foreach ((string _, IReadOnlyList<double> samples) in allSamples)
        {
            Assert.Equal(iterations, samples.Count);
        }
    }

    [Fact]
    public void StageTimingCollector_FirstOutput_RecordsFirstEventOnly()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Asr,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentAvailable,
            ItemIndex: 0));
        double? first = collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentAvailable);

        Thread.Sleep(10);
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Asr,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentAvailable,
            ItemIndex: 1));

        Assert.NotNull(first);
        Assert.Equal(first, collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentAvailable));
    }

    [Fact]
    public void StageTimingCollector_FirstOutput_NeverSubstitutesTerminalCompletion()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(StageName: StageNames.Asr, EventKind: PipelineProgressEventKind.Started));
        Thread.Sleep(2);
        collector.Report(new PipelineProgressEvent(StageName: StageNames.Asr, EventKind: PipelineProgressEventKind.Completed));

        Assert.Null(collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentAvailable));
        Assert.Null(collector.GetFirstOutputMilliseconds(PipelineOutputKind.PlayableAudioPersisted));
    }

    [Fact]
    public void StageTimingCollector_FirstOutput_TracksKindsIndependently()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Asr,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentAvailable,
            ItemIndex: 0));
        Thread.Sleep(5);
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.SpeakerAssignment,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentPersisted,
            ItemIndex: 0,
            RevisionId: Guid.NewGuid(),
            SegmentId: Guid.NewGuid()));

        double? available = collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentAvailable);
        double? persisted = collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentPersisted);

        Assert.NotNull(available);
        Assert.NotNull(persisted);
        Assert.True(persisted > available);
        Assert.Null(collector.GetFirstOutputMilliseconds(PipelineOutputKind.PlayableAudioPersisted));
    }

    [Fact]
    public void StageTimingCollector_FirstOutput_RejectsMalformedEvents()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        // Missing ItemIndex.
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Asr,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentAvailable));
        // Missing revision/segment ids on a persisted event.
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.SpeakerAssignment,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentPersisted,
            ItemIndex: 0));
        // Empty ids are not real identities.
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.SpeakerAssignment,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentPersisted,
            ItemIndex: 0,
            RevisionId: Guid.Empty,
            SegmentId: Guid.NewGuid()));
        // Missing artifact id on a playable event.
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Tts,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.PlayableAudioPersisted,
            ItemIndex: 0,
            SegmentId: Guid.NewGuid()));
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Tts,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.PlayableAudioPersisted,
            ItemIndex: 0,
            SegmentId: Guid.NewGuid(),
            ArtifactId: Guid.Empty));

        Assert.Null(collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentAvailable));
        Assert.Null(collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentPersisted));
        Assert.Null(collector.GetFirstOutputMilliseconds(PipelineOutputKind.PlayableAudioPersisted));
    }

    [Fact]
    public void StageTimingCollector_FirstOutput_RecordsValidPersistedAndPlayable()
    {
        var collector = new StageTimingCollector(Stopwatch.GetTimestamp());

        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.SpeakerAssignment,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.TranscriptSegmentPersisted,
            ItemIndex: 0,
            RevisionId: Guid.NewGuid(),
            SegmentId: Guid.NewGuid()));
        collector.Report(new PipelineProgressEvent(
            StageName: StageNames.Tts,
            EventKind: PipelineProgressEventKind.Progress,
            OutputKind: PipelineOutputKind.PlayableAudioPersisted,
            ItemIndex: 0,
            SegmentId: Guid.NewGuid(),
            ArtifactId: Guid.NewGuid()));

        Assert.NotNull(collector.GetFirstOutputMilliseconds(PipelineOutputKind.TranscriptSegmentPersisted));
        Assert.NotNull(collector.GetFirstOutputMilliseconds(PipelineOutputKind.PlayableAudioPersisted));
    }

    [Fact]
    public void MultiRunAggregation_PopulatesPercentileKeysInTimingsDictionary()
    {
        // Simulate 3 runs of stage timings collected across all 5 stages
        var stageSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase)
        {
            [StageNames.AudioPreparation] = [100.0, 110.0, 95.0],
            [StageNames.Separation] = [200.0, 210.0, 195.0],
            [StageNames.Asr] = [300.0, 310.0, 290.0],
            [StageNames.LipSync] = [150.0, 155.0, 145.0],
            [StageNames.Tts] = [250.0, 260.0, 240.0],
        };

        var timings = new Dictionary<string, double?>(StringComparer.Ordinal);

        // Perform aggregation using PercentileCalculator
        foreach ((string stage, List<double> samples) in stageSamples)
        {
            LatencyStatistics stats = PercentileCalculator.Calculate(samples);
            timings[$"stage:{stage}:min"] = stats.MinMilliseconds;
            timings[$"stage:{stage}:max"] = stats.MaxMilliseconds;
            timings[$"stage:{stage}:mean"] = stats.MeanMilliseconds;
            timings[$"stage:{stage}:p50"] = stats.P50Milliseconds;
            timings[$"stage:{stage}:p90"] = stats.P90Milliseconds;
            timings[$"stage:{stage}:p99"] = stats.P99Milliseconds;
            timings[$"stage:{stage}:throughput"] = stats.ThroughputUnitsPerSecond;
            timings[$"stage:{stage}:sampleCount"] = (double)stats.SampleCount;
            timings[$"stage:{stage}"] = stats.P50Milliseconds;
        }

        // Verify keys exist for all 5 canonical stages
        string[] requiredMetrics = ["min", "max", "mean", "p50", "p90", "p99", "throughput", "sampleCount"];
        foreach (string stage in stageSamples.Keys)
        {
            foreach (string key in requiredMetrics.Select(metric => $"stage:{stage}:{metric}"))
            {
                Assert.True(timings.ContainsKey(key), $"Timings dictionary missing expected key '{key}'.");
                Assert.NotNull(timings[key]);
            }

            // Verify math on ASR (samples [290, 300, 310])
            if (stage == StageNames.Asr)
            {
                Assert.Equal(290.0, timings[$"stage:{stage}:min"]);
                Assert.Equal(310.0, timings[$"stage:{stage}:max"]);
                Assert.Equal(300.0, timings[$"stage:{stage}:mean"]);
                Assert.Equal(300.0, timings[$"stage:{stage}:p50"]);
            }
        }
    }

    [Fact]
    public void ControlledStageBenchmarkMatrixOptionsParser_ParsesRunsOption()
    {
        using var error = new StringWriter();
        string[] args = ["fixture.mp4", "--output", "out", "--runs", "5"];

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            args,
            error,
            out ControlledStageBenchmarkMatrixOptions? options);

        Assert.True(parsed);
        Assert.NotNull(options);
        Assert.Equal(5, options.RunCount);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("not-a-number")]
    public void ControlledStageBenchmarkMatrixOptionsParser_RejectsInvalidRunsOption(string invalidValue)
    {
        using var error = new StringWriter();
        string[] args = ["fixture.mp4", "--output", "out", "--runs", invalidValue];

        bool parsed = ControlledStageBenchmarkMatrixOptionsParser.TryParse(
            args,
            error,
            out ControlledStageBenchmarkMatrixOptions? options);

        Assert.False(parsed);
        Assert.Null(options);
        Assert.Contains("Invalid run count", error.ToString(), StringComparison.Ordinal);
    }
}
