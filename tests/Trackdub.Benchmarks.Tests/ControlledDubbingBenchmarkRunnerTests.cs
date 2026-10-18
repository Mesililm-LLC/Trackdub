using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;

namespace Trackdub.Benchmarks.Tests;

public sealed class ControlledDubbingBenchmarkRunnerTests
{
    [Fact]
    public async Task Controlled_run_includes_resource_validation_in_evidenceAsync()
    {
        string directory = Path.Join(Path.GetTempPath(), $"telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string fixture = Path.Join(directory, "fixture.wav");
        await File.WriteAllBytesAsync(fixture, [1, 2, 3]);
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());
            var report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
                Stage = "audio-preparation",
            });
            Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
            var json = System.Text.Json.JsonSerializer.SerializeToElement(report);
            Assert.True(json.TryGetProperty("ResourceTelemetry", out var telemetry));
            Assert.NotEmpty(telemetry.EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_model_alias_is_rejected_before_running()
    {
        string fixture = Path.GetTempFileName();
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());

            // "whisper-onnx" is an engine family, not a model alias; the pipeline would
            // silently plan its default ASR model and the report would measure the wrong one.
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = Path.GetTempPath(),
                    Stage = "asr",
                    Model = "whisper-onnx",
                }));

            Assert.Contains("whisper-onnx", error.Message, StringComparison.Ordinal);
            Assert.Contains("whisper-small", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task Model_alias_for_a_different_task_is_rejected()
    {
        string fixture = Path.GetTempFileName();
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());

            // "kokoro" is a valid alias, but for TTS: the planner would keep its default ASR
            // model while the report claims the requested one.
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = Path.GetTempPath(),
                    Stage = "asr",
                    Model = "kokoro",
                }));

            Assert.Contains("kokoro", error.Message, StringComparison.Ordinal);
            Assert.Contains("asr", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task Model_task_mismatch_message_pins_the_required_task_for_non_obvious_stages()
    {
        string fixture = Path.GetTempFileName();
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());

            // "kokoro" is a TTS model; lip-sync requires the forced-alignment task. Asserting
            // the message pins ManifestTaskFor's non-obvious LipSync arm (stage string
            // "lip-sync" → task "forced-alignment"), which no rejection test covers today.
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = Path.GetTempPath(),
                    Stage = "lip-sync",
                    Model = "kokoro",
                }));

            Assert.Contains("forced-alignment", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task Mock_run_reports_ttft_from_structured_output_events_not_stage_completion()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ttft-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string fixture = Path.Join(directory, "fixture.wav");
        await File.WriteAllBytesAsync(fixture, [1, 2, 3]);
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());
            var report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
            });

            Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);

            double available = Assert.IsType<double>(
                report.TimingsMilliseconds["firstUsableTranscript"]);
            double persisted = Assert.IsType<double>(
                report.TimingsMilliseconds["firstPersistedTranscript"]);
            double playable = Assert.IsType<double>(
                report.TimingsMilliseconds["firstPlayableAudio"]);

            // The mock emits TranscriptSegmentAvailable at the start of the transcription stage
            // and TranscriptSegmentPersisted only after its ~30 ms simulated work completes.
            // A completion-derived TTFT would collapse this gap to ~0.
            Assert.True(
                persisted - available >= 10,
                $"firstPersistedTranscript ({persisted}) should lag firstUsableTranscript ({available}) by the stage work interval.");

            // PlayableAudioPersisted is emitted after the dubbing stage's simulated work
            // succeeds, inside the pipeline total.
            double pipeline = Assert.IsType<double>(report.TimingsMilliseconds["pipeline"]);
            Assert.True(playable >= 0 && playable <= pipeline,
                $"firstPlayableAudio ({playable}) should lie within the pipeline total ({pipeline}).");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mock_multi_run_reports_first_output_p50_instead_of_final_iteration()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ttft-p50-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string fixture = Path.Join(directory, "fixture.wav");
        await File.WriteAllBytesAsync(fixture, [1, 2, 3]);
        try
        {
            // Five runs keep the median stable when the first run has cold-start overhead.
            // The final run's deliberately large delay would dominate if the runner returned
            // that iteration instead of aggregating the measured first-output samples.
            const int runCount = 5;
            const int outlierDelayMs = 5000;
            var transcriptionStage = new SequencedTranscriptionStage(
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(outlierDelayMs));
            using var runner = new ControlledDubbingBenchmarkRunner(
                new NoHistory(),
                services =>
                {
                    MockDubbingPipelineServices.ConfigureMockPipeline(services);
                    services.Replace(ServiceDescriptor.Singleton<ITranscriptionStage>(transcriptionStage));
                });
            var report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
                RunCount = runCount,
            });

            Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
            double available = Assert.IsType<double>(
                report.TimingsMilliseconds["firstUsableTranscript"]);
            double persisted = Assert.IsType<double>(
                report.TimingsMilliseconds["firstPersistedTranscript"]);
            Assert.True(
                persisted - available < outlierDelayMs / 2,
                $"p50 first-output gap should exclude the final {outlierDelayMs} ms outlier, but was {persisted - available} ms.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Mock_run_with_failed_transcription_reports_no_output_timings()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ttft-fail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string fixture = Path.Join(directory, "fixture.wav");
        await File.WriteAllBytesAsync(fixture, [1, 2, 3]);
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(
                new NoHistory(),
                services => Trackdub.Benchmarks.Scenarios.MockDubbingPipelineServices.ConfigureMockPipeline(
                    services, mock => mock.FailStage = "transcription"));
            var report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
            });

            Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
            // A configured failure emits no structured output: the timings stay null even
            // though the mock artifact probe reports usable transcript/playable take.
            Assert.Null(report.TimingsMilliseconds["firstUsableTranscript"]);
            Assert.Null(report.TimingsMilliseconds["firstPersistedTranscript"]);
            Assert.Null(report.TimingsMilliseconds["firstPlayableAudio"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class SequencedTranscriptionStage : ITranscriptionStage
    {
        private readonly TimeSpan[] delays;
        private int index;

        public SequencedTranscriptionStage(params TimeSpan[] delays)
        {
            this.delays = delays;
        }

        public async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref index) - 1;
            await Task.Delay(delays[current], cancellationToken);
        }
    }

    private sealed class NoHistory : IBenchmarkEvidenceRepository
    {
        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BenchmarkEvidenceReport?>(null);

        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
            BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>([]);
    }
}
