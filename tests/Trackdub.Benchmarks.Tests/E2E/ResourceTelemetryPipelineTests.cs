using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Application.Benchmarking;
using Trackdub.Application.Dubbing;
using Trackdub.Application.Pipeline;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Persistence;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Domain.StageRuns;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.TestDoubles;

namespace Trackdub.Benchmarks.Tests.E2E;

[Trait("Category", "Slow")]
public sealed class ResourceTelemetryPipelineTests : IDisposable
{
    private static readonly ResourceTelemetryBounds ExactBounds = new()
    {
        MaxCpuPercent = 50,
        MaxWorkingSetBytes = 1000,
        MaxManagedAllocatedBytes = 10,
        MinAvailableVramMb = 500,
        MaxGpuBytes = 4096,
    };

    private readonly string root = Path.Join(Path.GetTempPath(), $"trackdub-resource-e2e-{Guid.NewGuid():N}");
    private readonly byte[] original = FakeWavHelper.MinimalPcm16();
    private readonly RecordingHistory history = new();
    private string Fixture => Path.Join(root, "source.wav");
    private string Output => Path.Join(root, "output");

    public ResourceTelemetryPipelineTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Fixture, original);
    }

    [Fact]
    public async Task Injected_monitor_factory_controls_process_and_stage_peaksAsync()
    {
        var factory = new FixedPeakFactory();
        using var runner = new ControlledDubbingBenchmarkRunner(history, services =>
        {
            services.AddSingleton<IResourceTelemetryCollector>(new CounterCollector());
            services.AddSingleton<IWorkingSetSampler>(new FixedWorkingSetSampler(1000));
        }, factory);
        var report = await runner.RunAsync(Options() with { ResourceTelemetryBounds = new() });
        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Equal(2999L, report.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal(2999d, Assert.Single(Assert.Single(report.ResourceTelemetry).Validation.Checks,
            check => check.Metric == "workingSetBytes").ObservedValue);
        Assert.True(factory.Created >= 2);
    }

    private sealed class FixedPeakFactory : IWorkingSetPeakMonitorFactory
    {
        public int Created { get; private set; }
        public IWorkingSetPeakMonitor Create(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval = null)
        {
            Created++;
            return new FixedPeak();
        }
        private sealed class FixedPeak : IWorkingSetPeakMonitor
        {
            public string? UnavailableReason => null;
            public string? SamplingWarning => null;
            public long? Stop() => 2999;
        }
    }

    [Fact]
    public async Task Runner_emits_typed_passed_evidence_at_exact_inclusive_budgetsAsync()
    {
        var collector = new CounterCollector();
        using var runner = CreateMockRunner(collector, gpuMemoryReader: new FixedGpuMemoryReader(4096));

        BenchmarkEvidenceReport report = await runner.RunAsync(Options());

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Null(report.Reason);
        Assert.Equal(ResourceTelemetryStatus.Passed, report.ResourceValidationStatus);
        Assert.Equal(ExactBounds, report.ResourceTelemetryBounds);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal("audio-prep", sample.Stage);
        Assert.Equal("measured", sample.Phase);
        Assert.Equal(1, sample.Iteration);
        Assert.Equal(1, sample.Attempt);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        AssertExactValidation(sample.Validation);
        // One capture per stage boundary; the report reuses the last reading.
        Assert.Equal(2, collector.CaptureCount);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
        Assert.Same(report, Assert.Single(history.Reports));
        AssertFixturePreserved(report, Output);
    }

    [Theory]
    [InlineData("cpuPercent", 50d, 49d)]
    [InlineData("workingSetBytes", 1000d, 999d)]
    [InlineData("managedAllocatedBytes", 10d, 9d)]
    [InlineData("availableVramMb", 500d, 501d)]
    [InlineData("gpuBytes", 4096d, 4095d)]
    public async Task Each_metric_budget_can_fail_report_without_rewriting_successful_stageAsync(string metric, double observed, double bound)
    {
        // Usage budgets are inclusive maxima (breach above); the VRAM floor is an inclusive
        // minimum (breach below), so its breaching bound sits one MB above the reading.
        ResourceTelemetryBounds bounds = metric switch
        {
            "cpuPercent" => ExactBounds with { MaxCpuPercent = bound },
            "workingSetBytes" => ExactBounds with { MaxWorkingSetBytes = (long)bound },
            "managedAllocatedBytes" => ExactBounds with { MaxManagedAllocatedBytes = (long)bound },
            "gpuBytes" => ExactBounds with { MaxGpuBytes = (long)bound },
            _ => ExactBounds with { MinAvailableVramMb = (long)bound },
        };
        using var runner = CreateMockRunner(new CounterCollector());

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with { ResourceTelemetryBounds = bounds });

        Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
        Assert.Equal(ResourceTelemetryStatus.Failed, report.ResourceValidationStatus);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        ResourceTelemetryCheck failure = Assert.Single(sample.Validation.Checks, check => check.Status == ResourceTelemetryStatus.Failed);
        Assert.Equal(metric, failure.Metric);
        Assert.Equal(observed, failure.ObservedValue);
        Assert.Equal(bound, failure.Threshold);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        Assert.Contains(metric, report.Reason, StringComparison.Ordinal);
        AssertFixturePreserved(report, Output);
    }

    [Fact]
    public async Task Sampled_working_set_peak_is_written_to_typed_telemetryAsync()
    {
        using var runner = CreateMockRunner(
            new CounterCollector(), workingSetSampler: new FixedWorkingSetSampler(2000));

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with
        {
            ResourceTelemetryBounds = new ResourceTelemetryBounds(),
        });

        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        ResourceTelemetryCheck workingSet = Assert.Single(sample.Validation.Checks,
            check => check.Metric == "workingSetBytes");
        Assert.Equal(2000d, workingSet.ObservedValue);
    }

    [Fact]
    public async Task Missing_counters_remain_unavailable_without_failing_successful_executionAsync()
    {
        using var runner = CreateMockRunner(
            new MissingCollector(), workingSetSampler: new ThrowingWorkingSetSampler());

        BenchmarkEvidenceReport report = await runner.RunAsync(Options());

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Null(report.Reason);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, report.ResourceValidationStatus);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, sample.Validation.Status);
        AssertNoUsage(sample.Validation, ResourceTelemetryStatus.Unavailable);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
        AssertFixturePreserved(report, Output);
    }

    [Fact]
    public async Task Earlier_measured_outlier_fails_report_even_when_final_iteration_passesAsync()
    {
        using var runner = CreateMockRunner(new CounterCollector(outlierPair: 0), gpuMemoryReader: new FixedGpuMemoryReader(4096));

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with { RunCount = 3 });

        Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
        Assert.Equal(ResourceTelemetryStatus.Failed, report.ResourceValidationStatus);
        Assert.Equal(new[] { 1, 2, 3 }, report.ResourceTelemetry.Select(sample => sample.Iteration));
        Assert.All(report.ResourceTelemetry, sample =>
        {
            Assert.Equal("measured", sample.Phase);
            Assert.Equal(1, sample.Attempt);
            Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        });
        Assert.Equal(ResourceTelemetryStatus.Failed, report.ResourceTelemetry[0].Validation.Status);
        AssertExactValidation(report.ResourceTelemetry[1].Validation);
        AssertExactValidation(report.ResourceTelemetry[2].Validation);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
        Assert.Equal(3d, report.TimingsMilliseconds["stage:audio-prep:sampleCount"]);
        Assert.Contains("audio-prep (measured, iteration 1, attempt 1)", report.Reason, StringComparison.Ordinal);
        Assert.Contains("cpuPercent", report.Reason, StringComparison.Ordinal);

        JsonElement json = JsonSerializer.SerializeToElement(report, BenchmarkReportWriter.SerializerOptions);
        JsonElement cpu = json.GetProperty("ResourceTelemetry")[0].GetProperty("Validation")
            .GetProperty("Checks").EnumerateArray().Single(check => check.GetProperty("Metric").GetString() == "cpuPercent");
        Assert.Equal("Failed", cpu.GetProperty("Status").GetString());
        Assert.Equal(75d, cpu.GetProperty("ObservedValue").GetDouble());
        Assert.Equal(50d, cpu.GetProperty("Threshold").GetDouble());
        AssertFixturePreserved(report, Output);
    }

    [Fact]
    public async Task Earlier_attempt_outlier_survives_successful_retry_and_outcome_reconciliationAsync()
    {
        var collector = new CounterCollector(outlierPair: 0);
        using var runner = CreateScriptedRunner(collector, (options, progress) =>
        {
            string stage = Assert.Single(options.StageFilter!);
            progress!.Report(new(stage, PipelineProgressEventKind.Started));
            progress.Report(new(stage, PipelineProgressEventKind.Failed, Message: "retryable execution failure"));
            progress.Report(new(stage, PipelineProgressEventKind.Started));
            progress.Report(new(stage, PipelineProgressEventKind.Completed));
            return Result(stage, StageStatus.Succeeded);
        }, gpuMemoryReader: new FixedGpuMemoryReader(4096));

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with { Mock = false });

        Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
        Assert.Equal(2, report.ResourceTelemetry.Count);
        Assert.Equal(new[] { 1, 2 }, report.ResourceTelemetry.Select(sample => sample.Attempt));
        Assert.Equal(BenchmarkEvidenceStatus.Failed, report.ResourceTelemetry[0].ExecutionStatus);
        Assert.Equal("retryable execution failure", report.ResourceTelemetry[0].Reason);
        Assert.Equal(ResourceTelemetryStatus.Failed, report.ResourceTelemetry[0].Validation.Status);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.ResourceTelemetry[1].ExecutionStatus);
        AssertExactValidation(report.ResourceTelemetry[1].Validation);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
        Assert.Contains("iteration 1, attempt 1", report.Reason, StringComparison.Ordinal);
        Assert.Equal(4, collector.CaptureCount);
        AssertFixturePreserved(report, Output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unstarted_skip_has_explicit_reason_and_no_invented_usageAsync(bool emitTerminalEvent)
    {
        const string reason = "Disabled before execution; original audio retained.";
        var collector = new CounterCollector();
        using var runner = CreateScriptedRunner(collector, (options, progress) =>
        {
            string stage = Assert.Single(options.StageFilter!);
            if (emitTerminalEvent)
                progress!.Report(new(stage, PipelineProgressEventKind.Skipped, Message: reason));
            return Result(stage, StageStatus.Skipped, reason);
        });

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with { Mock = false });

        Assert.Equal(BenchmarkEvidenceStatus.Skipped, report.Status);
        Assert.Equal(reason, report.Reason);
        Assert.Equal(ResourceTelemetryStatus.Skipped, report.ResourceValidationStatus);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal(BenchmarkEvidenceStatus.Skipped, sample.ExecutionStatus);
        Assert.Equal(0, sample.Attempt);
        Assert.Equal(reason, sample.Reason);
        AssertNoUsage(sample.Validation, ResourceTelemetryStatus.Skipped);
        Assert.All(sample.Validation.Checks, check => Assert.Equal(reason, check.Reason));
        Assert.Equal(0, collector.CaptureCount);
        AssertFixturePreserved(report, Output);
    }

    [Fact]
    public async Task Outcome_reconciles_started_stage_without_terminal_progressAsync()
    {
        var collector = new CounterCollector();
        using var runner = CreateScriptedRunner(collector, (options, progress) =>
        {
            string stage = Assert.Single(options.StageFilter!);
            progress!.Report(new(stage, PipelineProgressEventKind.Started));
            return Result(stage, StageStatus.Succeeded);
        }, gpuMemoryReader: new FixedGpuMemoryReader(4096));

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with { Mock = false });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, sample.ExecutionStatus);
        Assert.Equal(1, sample.Attempt);
        AssertExactValidation(sample.Validation);
        Assert.Equal(2, collector.CaptureCount);
    }

    [Fact]
    public async Task Execution_and_resource_failures_preserve_both_reasons_and_original_bytesAsync()
    {
        const string failure = "Decoder refused the fixture; original retained.";
        using var runner = CreateMockRunner(new CounterCollector(outlierPair: 0), options =>
        {
            options.FailStage = "audio-prep";
            options.FailureReason = failure;
        });

        BenchmarkEvidenceReport report = await runner.RunAsync(Options());

        Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
        Assert.Contains(failure, report.Reason, StringComparison.Ordinal);
        Assert.Contains("Resource validation failed", report.Reason, StringComparison.Ordinal);
        Assert.Contains("cpuPercent", report.Reason, StringComparison.Ordinal);
        BenchmarkEvidenceStage stage = Assert.Single(report.Stages);
        Assert.Equal(BenchmarkEvidenceStatus.Failed, stage.Status);
        Assert.Equal(failure, stage.Reason);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal(BenchmarkEvidenceStatus.Failed, sample.ExecutionStatus);
        Assert.Equal(failure, sample.Reason);
        Assert.Equal(ResourceTelemetryStatus.Failed, sample.Validation.Status);
        AssertFixturePreserved(report, Output);
    }

    [Theory]
    [InlineData("warm-host", "warmup")]
    [InlineData("artifact-resume", "priming")]
    public async Task Preparation_outlier_is_retained_but_not_counted_as_measured_timingAsync(string mode, string phase)
    {
        // ASR prepares VAD and diarization first, then warms/primes ASR before two measured runs.
        using var runner = CreateMockRunner(new CounterCollector(outlierPair: 2), gpuMemoryReader: new FixedGpuMemoryReader(4096));

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with
        {
            Stage = StageNames.Asr,
            Mode = mode,
            RunCount = 2,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
        Assert.Equal(2, report.ResourceTelemetry.Count(sample => sample.Phase == "prerequisites"));
        BenchmarkStageResourceTelemetry preparation = Assert.Single(report.ResourceTelemetry, sample => sample.Phase == phase);
        Assert.Equal(0, preparation.Iteration);
        Assert.Equal(ResourceTelemetryStatus.Failed, preparation.Validation.Status);
        BenchmarkStageResourceTelemetry[] measured = report.ResourceTelemetry.Where(sample => sample.Phase == "measured").ToArray();
        Assert.Equal(new[] { 1, 2 }, measured.Select(sample => sample.Iteration));
        Assert.All(measured, sample => AssertExactValidation(sample.Validation));
        Assert.Equal(2d, report.TimingsMilliseconds[$"stage:{StageNames.Asr}:sampleCount"]);
        Assert.Contains($"({phase}, iteration 0, attempt 1)", report.Reason, StringComparison.Ordinal);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
    }

    [Fact]
    public async Task Matrix_writer_serializes_validation_and_raw_cpu_normalization_for_every_stageAsync()
    {
        using var matrixRunner = new ControlledStageBenchmarkMatrixRunner(
            CreateMockRunner(new CounterCollector(), gpuMemoryReader: new FixedGpuMemoryReader(4096)));
        ControlledStageBenchmarkMatrixReport matrix = await matrixRunner.RunAsync(new()
        {
            FixturePath = Fixture,
            OutputDirectory = Output,
            Stages = DubbingPipelineStages.ExtendedStageOrder,
            Mock = true,
            ReuseEngineCache = true,
            RunCount = 2,
            ResourceTelemetryBounds = ExactBounds,
        });
        await BenchmarkReportWriter.WriteAsync(matrix, CancellationToken.None);

        Assert.Equal(BenchmarkEvidenceStatus.Completed, matrix.Status);
        Assert.Equal(DubbingPipelineStages.ExtendedStageOrder, matrix.Results.Select(result => result.Stage));
        using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(matrix.ReportPath));
        Assert.Equal("Completed", json.RootElement.GetProperty("Status").GetString());
        JsonElement[] results = json.RootElement.GetProperty("Results").EnumerateArray().ToArray();
        Assert.Equal(DubbingPipelineStages.ExtendedStageOrder.Count, results.Length);
        foreach (JsonElement result in results)
        {
            string stage = result.GetProperty("Stage").GetString()!;
            JsonElement evidence = result.GetProperty("Evidence");
            Assert.Equal("Completed", evidence.GetProperty("Status").GetString());
            Assert.Equal("Passed", evidence.GetProperty("ResourceValidationStatus").GetString());
            JsonElement bounds = evidence.GetProperty("ResourceTelemetryBounds");
            Assert.Equal(50d, bounds.GetProperty("MaxCpuPercent").GetDouble());
            Assert.Equal(1000, bounds.GetProperty("MaxWorkingSetBytes").GetInt64());
            Assert.Equal(10, bounds.GetProperty("MaxManagedAllocatedBytes").GetInt64());
            Assert.Equal(500, bounds.GetProperty("MinAvailableVramMb").GetInt64());
            Assert.Contains("processor count", evidence.GetProperty("Configuration").GetProperty("cpuNormalization").GetString(), StringComparison.Ordinal);
            JsonElement[] samples = evidence.GetProperty("ResourceTelemetry").EnumerateArray().ToArray();
            JsonElement[] measured = samples.Where(sample => sample.GetProperty("Phase").GetString() == "measured").ToArray();
            Assert.Equal(new[] { 1, 2 }, measured.Select(sample => sample.GetProperty("Iteration").GetInt32()));
            Assert.All(measured, sample => Assert.Equal(MockDubbingPipelineServices.CanonicalBenchmarkStage(stage), sample.GetProperty("Stage").GetString()));
            foreach (JsonElement sample in samples)
            {
                Assert.Equal("Completed", sample.GetProperty("ExecutionStatus").GetString());
                Assert.Equal(1, sample.GetProperty("Attempt").GetInt32());
                JsonElement validation = sample.GetProperty("Validation");
                Assert.Equal("Passed", validation.GetProperty("Status").GetString());
                Assert.Equal(200d, validation.GetProperty("CpuTimeMilliseconds").GetDouble());
                Assert.Equal(100d, validation.GetProperty("ElapsedMilliseconds").GetDouble());
                Assert.Equal(4, validation.GetProperty("ProcessorCount").GetInt32());
                ResourceTelemetryValidation typed = validation.Deserialize<ResourceTelemetryValidation>(BenchmarkReportWriter.SerializerOptions)!;
                AssertExactValidation(typed);
            }
            Assert.Equal(2, result.GetProperty("Statistics").GetProperty("SampleCount").GetInt32());
            AssertFixturePreserved(matrix.Results.Single(item => item.Stage == stage).Evidence, Path.Join(Output, stage));
        }
    }

    [Fact]
    public async Task Real_engine_missing_prerequisite_preflight_marks_requested_measurement_skippedAsync()
    {
        var checker = new FakePipelinePreFlightChecker();
        checker.BlockStage(StageNames.Vad, new InvalidOperationException("Required VAD model is missing."));
        var collector = new CounterCollector();
        using var runner = new ControlledDubbingBenchmarkRunner(history, services =>
        {
            ConfigureRealEngine(services, checker);
            services.AddSingleton<IResourceTelemetryCollector>(collector);
        });

        BenchmarkEvidenceReport report = await runner.RunAsync(Options() with { Mock = false, Stage = StageNames.Asr });

        Assert.Contains(StageNames.Vad, checker.CheckedStageNames);
        Assert.DoesNotContain(StageNames.Asr, checker.CheckedStageNames);
        Assert.Equal(BenchmarkEvidenceStatus.Skipped, report.Status);
        Assert.Contains("Prerequisite preparation", report.Reason, StringComparison.Ordinal);
        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal(StageNames.Asr, sample.Stage);
        Assert.Equal("measured", sample.Phase);
        Assert.Equal(BenchmarkEvidenceStatus.Skipped, sample.ExecutionStatus);
        Assert.Equal(report.Reason, sample.Reason);
        Assert.Equal(ResourceTelemetryStatus.Skipped, sample.Validation.Status);
        Assert.Null(sample.Validation.CpuTimeMilliseconds);
        Assert.Null(sample.Validation.ElapsedMilliseconds);
        Assert.Null(sample.Validation.ProcessorCount);
        Assert.All(sample.Validation.Checks, check =>
        {
            Assert.Null(check.ObservedValue);
            Assert.Equal(ResourceTelemetryStatus.Skipped, check.Status);
            Assert.False(string.IsNullOrWhiteSpace(check.Reason));
        });
        Assert.Equal(0, collector.CaptureCount);
        AssertFixturePreserved(report, Output);
    }

    [Fact]
    public async Task Real_engine_disabled_and_missing_transcript_stages_keep_execution_separate_from_usageAsync()
    {
        using var host = HeadlessDubbingHost.Create(new()
        {
            ServiceConfigurator = services => ConfigureRealEngine(services, new FakePipelinePreFlightChecker()),
        });
        var collector = new CounterCollector();
        var capture = new StageResourceTelemetryCapture(collector, new ResourceTelemetryValidator(), ExactBounds, "measured", 1);
        DubbingPipelineEngine engine = host.CreateEngine();

        // Separation requires a media spine, so the real engine imports the WAV but no model runs.
        DubbingRunResult result = await engine.ExecuteAsync(new()
        {
            SourceMediaPath = Fixture,
            ProjectOutputDirectory = Path.Join(root, "real-skips.trackdub"),
            TargetLanguageCode = "es",
            StageFilter = [StageNames.Translation, StageNames.Separation],
            EnableStemSeparation = false,
            ForceRerun = true,
        }, capture);
        capture.CompleteOutcomes(result.StageOutcomes);

        Assert.Equal(2, result.StageOutcomes.Count);
        StageOutcome disabled = Assert.Single(result.StageOutcomes, outcome => outcome.StageName == StageNames.Separation);
        Assert.Equal(StageStatus.Skipped, disabled.Status);
        Assert.Equal(StageSkipReasonCodes.DisabledByOption, disabled.ReasonCode);
        StageOutcome missingTranscript = Assert.Single(result.StageOutcomes, outcome => outcome.StageName == StageNames.Translation);
        Assert.Equal(StageStatus.Skipped, missingTranscript.Status);
        Assert.Equal(StageSkipReasonCodes.NoTranscriptSegments, missingTranscript.ReasonCode);
        Assert.Equal(2, capture.Snapshot().Count);
        Assert.All(capture.Snapshot(), sample =>
        {
            Assert.Equal(BenchmarkEvidenceStatus.Skipped, sample.ExecutionStatus);
            Assert.Equal(1, sample.Attempt);
            // Reconciliation keeps the structured code queryable and the specific cause readable.
            Assert.Contains(result.StageOutcomes.Single(outcome => outcome.StageName == sample.Stage).ReasonCode!,
                sample.Reason!, StringComparison.Ordinal);
            // These stages did start: report their actual boundary counters, unlike unstarted skips.
            AssertExactValidation(sample.Validation);
        });
        Assert.All(result.StageOutcomes, outcome => Assert.Empty(outcome.ArtifactPaths));
        Assert.Equal(original, File.ReadAllBytes(Fixture));
    }

    [Fact]
    public async Task Real_engine_execution_failure_records_usage_but_downstream_skip_does_notAsync()
    {
        using var host = HeadlessDubbingHost.Create(new()
        {
            ServiceConfigurator = services => ConfigureRealEngine(services, new FakePipelinePreFlightChecker()),
        });
        var collector = new CounterCollector(outlierPair: 0);
        var capture = new StageResourceTelemetryCapture(collector, new ResourceTelemetryValidator(), ExactBounds, "measured", 1);
        DubbingPipelineEngine engine = host.CreateEngine();

        // Neither stage imports media. With no project record, Translation fails in the real
        // workspace, and the engine's prerequisite gate must skip TTS before its start boundary.
        DubbingRunResult result = await engine.ExecuteAsync(new()
        {
            SourceMediaPath = Fixture,
            ProjectOutputDirectory = Path.Join(root, "real-failure.trackdub"),
            TargetLanguageCode = "es",
            StageFilter = [StageNames.Translation, StageNames.Tts],
            ForceRerun = true,
        }, capture);
        capture.CompleteOutcomes(result.StageOutcomes);

        StageOutcome failure = Assert.Single(result.StageOutcomes, outcome => outcome.StageName == StageNames.Translation);
        Assert.Equal(StageStatus.Failed, failure.Status);
        Assert.Equal("STAGE_FAILED", failure.ReasonCode);
        StageOutcome skipped = Assert.Single(result.StageOutcomes, outcome => outcome.StageName == StageNames.Tts);
        Assert.Equal(StageStatus.Skipped, skipped.Status);
        Assert.Equal(StageSkipReasonCodes.PrerequisiteFailed, skipped.ReasonCode);
        Assert.Equal(2, capture.Snapshot().Count);
        BenchmarkStageResourceTelemetry failedSample = Assert.Single(capture.Snapshot(), sample => sample.Stage == StageNames.Translation);
        Assert.Equal(BenchmarkEvidenceStatus.Failed, failedSample.ExecutionStatus);
        Assert.False(string.IsNullOrWhiteSpace(failedSample.Reason));
        Assert.Equal(ResourceTelemetryStatus.Failed, failedSample.Validation.Status);
        ResourceTelemetryCheck cpu = Assert.Single(failedSample.Validation.Checks, check => check.Metric == "cpuPercent");
        Assert.Equal(75d, cpu.ObservedValue);
        Assert.Equal(50d, cpu.Threshold);
        BenchmarkStageResourceTelemetry skippedSample = Assert.Single(capture.Snapshot(), sample => sample.Stage == StageNames.Tts);
        Assert.Equal(BenchmarkEvidenceStatus.Skipped, skippedSample.ExecutionStatus);
        Assert.Equal(0, skippedSample.Attempt);
        Assert.Contains(StageNames.Translation, skippedSample.Reason, StringComparison.Ordinal);
        AssertNoUsage(skippedSample.Validation, ResourceTelemetryStatus.Skipped);
        Assert.Equal(2, collector.CaptureCount);
        Assert.All(result.StageOutcomes, outcome => Assert.Empty(outcome.ArtifactPaths));
        Assert.Equal(original, File.ReadAllBytes(Fixture));
    }

    [Fact]
    public async Task Repository_roundtrip_scrubs_nested_telemetry_paths_without_losing_numeric_evidenceAsync()
    {
        const string windowsPath = @"C:\private\source.wav";
        const string unixPath = "/private/models/gpu.bin";
        var repository = new BenchmarkEvidenceRepository(new SqliteUserBenchmarkDatabase(Path.Join(root, "history")));
        using var runner = CreateMockRunner(new CounterCollector(gpuUnavailableReason: $"GPU counter unavailable at {unixPath}"), options =>
        {
            options.FailStage = "audio-prep";
            options.FailureReason = $"Could not decode {windowsPath}";
        }, repository);

        BenchmarkEvidenceReport report = await runner.RunAsync(Options());
        BenchmarkEvidenceReport? restored = await repository.GetAsync(report.RunId);

        Assert.NotNull(restored);
        Assert.Equal(report.RunId, restored.RunId);
        Assert.Equal(BenchmarkEvidenceStatus.Failed, restored.Status);
        Assert.Equal(ResourceTelemetryStatus.Unavailable, restored.ResourceValidationStatus);
        Assert.Equal(ExactBounds, restored.ResourceTelemetryBounds);
        // Paths are scrubbed at the persistence boundary, so the in-memory report keeps the
        // original cause and only the restored report is path-free.
        Assert.Contains(windowsPath, report.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(restored), StringComparison.Ordinal);
        Assert.Contains("[path]", restored.Reason, StringComparison.Ordinal);
        Assert.Contains("[path]", Assert.Single(restored.Stages).Reason, StringComparison.Ordinal);
        BenchmarkStageResourceTelemetry sample = Assert.Single(restored.ResourceTelemetry);
        Assert.Contains("[path]", sample.Reason, StringComparison.Ordinal);
        Assert.Equal(1, sample.Iteration);
        Assert.Equal(1, sample.Attempt);
        Assert.Equal(200d, sample.Validation.CpuTimeMilliseconds);
        Assert.Equal(100d, sample.Validation.ElapsedMilliseconds);
        Assert.Equal(4, sample.Validation.ProcessorCount);
        AssertCheck(sample.Validation, "cpuPercent", 50);
        AssertCheck(sample.Validation, "workingSetBytes", 1000);
        AssertCheck(sample.Validation, "managedAllocatedBytes", 10);
        ResourceTelemetryCheck gpu = Assert.Single(sample.Validation.Checks, check => check.Metric == "availableVramMb");
        Assert.Equal(ResourceTelemetryStatus.Unavailable, gpu.Status);
        Assert.Null(gpu.ObservedValue);
        Assert.Equal(500d, gpu.Threshold);
        Assert.Contains("[path]", gpu.Reason, StringComparison.Ordinal);
        string serialized = JsonSerializer.Serialize(restored);
        Assert.DoesNotContain("private", serialized, StringComparison.Ordinal);
        Assert.Equal(report.RunId, Assert.Single(await repository.ListRecentAsync(BenchmarkEvidenceKind.Benchmark, 10)).RunId);
        AssertFixturePreserved(report, Output);
    }

    private ControlledDubbingBenchmarkOptions Options() => new()
    {
        FixturePath = Fixture,
        OutputDirectory = Output,
        Stage = StageNames.AudioPreparation,
        Mock = true,
        ReuseEngineCache = true,
        ResourceTelemetryBounds = ExactBounds,
    };

    private ControlledDubbingBenchmarkRunner CreateMockRunner(
        IResourceTelemetryCollector collector,
        Action<MockPipelineOptions>? configure = null,
        IBenchmarkEvidenceRepository? repository = null,
        IWorkingSetSampler? workingSetSampler = null,
        IProcessGpuMemoryReader? gpuMemoryReader = null) => new(repository ?? history, services =>
    {
        MockDubbingPipelineServices.ConfigureMockPipeline(services, options =>
        {
            foreach (string key in options.SimulatedStageLatencies.Keys.ToArray())
                options.SimulatedStageLatencies[key] = TimeSpan.Zero;
            foreach (string key in options.SimulatedMemoryAllocations.Keys.ToArray())
                options.SimulatedMemoryAllocations[key] = 0;
            configure?.Invoke(options);
        });
        services.AddSingleton<IResourceTelemetryCollector>(collector);
        services.AddSingleton<IWorkingSetSampler>(workingSetSampler ?? new FixedWorkingSetSampler(1000));
        if (gpuMemoryReader is not null)
            services.AddSingleton<IProcessGpuMemoryReader>(gpuMemoryReader);
    });

    private ControlledDubbingBenchmarkRunner CreateScriptedRunner(
        IResourceTelemetryCollector collector,
        Func<DubbingSessionOptions, IProgress<PipelineProgressEvent>?, DubbingRunResult> execute,
        IProcessGpuMemoryReader? gpuMemoryReader = null) => new(history, services =>
    {
        services.AddSingleton<IResourceTelemetryCollector>(collector);
        services.AddSingleton<IWorkingSetSampler>(new FixedWorkingSetSampler(1000));
        if (gpuMemoryReader is not null)
            services.AddSingleton<IProcessGpuMemoryReader>(gpuMemoryReader);
        services.AddSingleton<IDubbingPipelineService>(new ScriptedPipeline(execute));
    });

    private static void ConfigureRealEngine(IServiceCollection services, FakePipelinePreFlightChecker checker)
    {
        services.RemoveAll<IPipelineReadinessService>();
        services.Replace(ServiceDescriptor.Singleton<IPipelinePreFlightChecker>(checker));
        services.Replace(ServiceDescriptor.Singleton<IMediaProbe, MockMediaProbe>());
    }

    private static DubbingRunResult Result(string stage, StageStatus status, string? reason = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new()
        {
            RunId = Guid.NewGuid(),
            StartTime = now,
            EndTime = now,
            OverallStatus = DubbingRunStatus.Succeeded,
            StageOutcomes = [new()
            {
                StageName = stage,
                Status = status,
                StartTime = now,
                EndTime = now,
                ReasonCode = reason,
                ArtifactPaths = [],
            }],
        };
    }

    private static void AssertExactValidation(ResourceTelemetryValidation validation)
    {
        Assert.Equal(ResourceTelemetryStatus.Passed, validation.Status);
        Assert.Equal(200d, validation.CpuTimeMilliseconds);
        Assert.Equal(100d, validation.ElapsedMilliseconds);
        Assert.Equal(4, validation.ProcessorCount);
        Assert.Equal(5, validation.Checks.Count);
        AssertCheck(validation, "cpuPercent", 50);
        AssertCheck(validation, "workingSetBytes", 1000);
        AssertCheck(validation, "managedAllocatedBytes", 10);
        AssertCheck(validation, "availableVramMb", 500);
        AssertCheck(validation, "gpuBytes", 4096);
    }

    private static void AssertCheck(ResourceTelemetryValidation validation, string metric, double expected)
    {
        ResourceTelemetryCheck check = Assert.Single(validation.Checks, check => check.Metric == metric);
        Assert.Equal(ResourceTelemetryStatus.Passed, check.Status);
        Assert.Equal(expected, check.ObservedValue);
        Assert.Equal(expected, check.Threshold);
        // A passing check may carry only the advisory sampling-cadence note, which depends on host load.
        Assert.True(check.Reason is null || check.Reason.StartsWith("Working-set sampling cadence dilated", StringComparison.Ordinal),
            check.Reason);
    }

    private static void AssertNoUsage(ResourceTelemetryValidation validation, ResourceTelemetryStatus status)
    {
        Assert.Null(validation.CpuTimeMilliseconds);
        Assert.Null(validation.ElapsedMilliseconds);
        Assert.Null(validation.ProcessorCount);
        Assert.Equal(5, validation.Checks.Count);
        Assert.All(validation.Checks, check =>
        {
            Assert.Equal(status, check.Status);
            Assert.Null(check.ObservedValue);
            Assert.NotNull(check.Threshold);
            Assert.False(string.IsNullOrWhiteSpace(check.Reason));
        });
    }

    private void AssertFixturePreserved(BenchmarkEvidenceReport report, string output)
    {
        Assert.Equal(original, File.ReadAllBytes(Fixture));
        string project = Assert.Single(Directory.GetDirectories(Path.Join(output, "projects")));
        Assert.Equal(original, File.ReadAllBytes(Path.Join(project, "fixture.wav")));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant(), report.FixtureSha256);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { /* A pooled SQLite handle may briefly retain a test-local file on Windows. */ }
        catch (UnauthorizedAccessException) { /* Best-effort cleanup of this test's private directory. */ }
    }

    private sealed class ThrowingWorkingSetSampler : IWorkingSetSampler
    {
        public long CaptureWorkingSetBytes() => throw new InvalidOperationException("Working-set telemetry unavailable.");
    }

    private sealed class FixedWorkingSetSampler(long workingSetBytes) : IWorkingSetSampler
    {
        public long CaptureWorkingSetBytes() => workingSetBytes;
    }

    /// <summary>Reports a constant process-GPU reading so interval peak sampling is real.</summary>
    private sealed class FixedGpuMemoryReader(long bytes) : IProcessGpuMemoryReader
    {
        public string UnavailableReason => "Test double: fixed reading.";

        public long? ReadDedicatedGpuMemoryBytes() => bytes;
    }

    private sealed class MissingCollector : IResourceTelemetryCollector
    {
        public ResourceUsageSnapshot Capture() => new();
    }

    private sealed class CounterCollector(int? outlierPair = null, string? gpuUnavailableReason = null) : IResourceTelemetryCollector
    {
        public int CaptureCount { get; private set; }

        public ResourceUsageSnapshot Capture()
        {
            int index = CaptureCount++;
            int pair = index / 2;
            bool end = index % 2 == 1;
            double baseCpuTimeMilliseconds = pair * 1000d;
            double endCpuTimeMilliseconds = end ? (pair == outlierPair ? 300d : 200d) : 0d;
            double cpuTimeMilliseconds = baseCpuTimeMilliseconds + endCpuTimeMilliseconds;
            return new()
            {
                CpuTimeMilliseconds = cpuTimeMilliseconds,
                MonotonicMilliseconds = index * 100d,
                ProcessorCount = 4,
                WorkingSetBytes = 1000,
                ManagedAllocatedBytes = index * 10L,
                AvailableVramMb = gpuUnavailableReason is null ? 500 : null,
                VramUnavailableReason = gpuUnavailableReason,
                GpuBytes = 4096,
            };
        }
    }

    private sealed class ScriptedPipeline(
        Func<DubbingSessionOptions, IProgress<PipelineProgressEvent>?, DubbingRunResult> execute) : IDubbingPipelineService
    {
        public Task<DubbingRunResult> ExecuteAsync(DubbingSessionOptions options, IProgress<PipelineProgressEvent>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(execute(options, progress));
        }

        public IReadOnlyList<StageRunRecord> GetStageRuns(string project, string? requestedProvider, string? requestedModel) => [];
    }

    private sealed class RecordingHistory : IBenchmarkEvidenceRepository
    {
        public List<BenchmarkEvidenceReport> Reports { get; } = [];
        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }

        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Reports.SingleOrDefault(report => report.RunId == runId));

        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>(Reports.Where(report => kind is null || report.Kind == kind).Take(limit).ToArray());
    }
}
