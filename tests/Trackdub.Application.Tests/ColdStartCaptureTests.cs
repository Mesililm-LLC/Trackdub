using Trackdub.Contracts.Diagnostics;

namespace Trackdub.Application.Tests;

public sealed class ColdStartCaptureTests
{
    [Fact]
    public void Marks_without_active_capture_are_no_ops()
    {
        // Benchmarks, tests, and ad-hoc runs have no capture; marks must never throw.
        ColdStartCapture.MarkInferenceCompleted("onnx-run");
        ColdStartCapture.MarkFirstStageStart("vad");
        Assert.Null(ColdStartCapture.Current);
    }

    [Fact]
    public void First_inference_mark_wins_and_later_marks_are_ignored()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "vad"))
        {
            ColdStartCapture.MarkInferenceCompleted("first:run");
            ColdStartCapture.MarkInferenceCompleted("second:run");
        }

        PipelineColdStartTiming timing = capture.BuildTiming();
        Assert.Equal("first:run", timing.FirstInferenceSource);
        Assert.Equal(ColdStartPhase.Stage, timing.FirstInferencePhase);
        Assert.Equal("vad", timing.FirstInferenceStageName);
        Assert.NotNull(timing.FirstInferenceCompletionElapsedMs);
        Assert.True(timing.FirstInferenceCompletionElapsedMs >= 0);
    }

    [Fact]
    public void Smoke_and_stage_completions_record_separate_milestones()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        {
            using (ColdStartCapture.EnterPhase(ColdStartPhase.Smoke))
            {
                ColdStartCapture.MarkInferenceCompleted("smoke:genai");
            }

            using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "asr"))
            {
                ColdStartCapture.MarkInferenceCompleted("onnx-run");
            }
        }

        PipelineColdStartTiming timing = capture.BuildTiming();
        Assert.Equal("smoke:genai", timing.FirstInferenceSource);
        Assert.Equal(ColdStartPhase.Smoke, timing.FirstInferencePhase);
        Assert.Equal("smoke:genai", timing.FirstSmokeInferenceSource);
        Assert.NotNull(timing.FirstSmokeInferenceCompletionElapsedMs);
        Assert.Equal("onnx-run", timing.FirstStageInferenceSource);
        Assert.Null(timing.FirstInferenceStageName); // the first inference was the smoke pass
        Assert.NotNull(timing.FirstStageInferenceCompletionElapsedMs);
    }

    [Fact]
    public void Unclassified_marks_record_the_unclassified_milestone()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        {
            ColdStartCapture.MarkInferenceCompleted("inference-worker:tts");
        }

        PipelineColdStartTiming timing = capture.BuildTiming();
        Assert.Equal("inference-worker:tts", timing.FirstUnclassifiedInferenceSource);
        Assert.Equal(ColdStartPhase.Unclassified, timing.FirstInferencePhase);
        Assert.Null(timing.FirstInferenceStageName);
    }

    [Fact]
    public async Task Marks_flow_into_spawned_async_work()
    {
        // Inference completes on worker threads (Task.Run, prefetch loops, generator
        // loops); the ambient capture must flow there like BenchmarkPhaseCapture.
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        {
            await Task.Run(
                () => ColdStartCapture.MarkInferenceCompleted("onnx-run"),
                TestContext.Current.CancellationToken);
        }

        Assert.Equal("onnx-run", capture.BuildTiming().FirstInferenceSource);
    }

    [Fact]
    public void Nested_activations_are_isolated()
    {
        var outer = new ColdStartCapture();
        var inner = new ColdStartCapture();
        using (ColdStartCapture.Activate(outer))
        {
            using (ColdStartCapture.Activate(inner))
            {
                ColdStartCapture.MarkInferenceCompleted("inner:run");
            }

            ColdStartCapture.MarkInferenceCompleted("outer:run");
        }

        Assert.Equal("inner:run", inner.BuildTiming().FirstInferenceSource);
        Assert.Equal("outer:run", outer.BuildTiming().FirstInferenceSource);
    }

    [Fact]
    public void Phase_scopes_nest_and_restore()
    {
        Assert.Null(ColdStartCapture.CurrentPhase);
        using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "vad"))
        {
            Assert.Equal(ColdStartPhase.Stage, ColdStartCapture.CurrentPhase);
            using (ColdStartCapture.EnterPhase(ColdStartPhase.Smoke))
            {
                Assert.Equal(ColdStartPhase.Smoke, ColdStartCapture.CurrentPhase);
            }

            Assert.Equal(ColdStartPhase.Stage, ColdStartCapture.CurrentPhase);
        }

        Assert.Null(ColdStartCapture.CurrentPhase);
    }

    [Fact]
    public void First_stage_start_is_recorded_once()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        {
            ColdStartCapture.MarkFirstStageStart("vad");
            ColdStartCapture.MarkFirstStageStart("asr");
        }

        PipelineColdStartTiming timing = capture.BuildTiming();
        Assert.Equal("vad", timing.FirstStageStartedStageName);
        Assert.NotNull(timing.FirstStageStartElapsedMs);
        Assert.True(timing.FirstStageStartElapsedMs >= 0);
    }

    [Fact]
    public void RecordPhase_aggregates_by_name_and_orders_by_total()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        {
            ColdStartCapture.RecordPhase("session-create", 40);
            ColdStartCapture.RecordPhase("session-create", 60);
            ColdStartCapture.RecordPhase("provider-bootstrap", 1250);
            ColdStartCapture.RecordPhase("runtime-plan", 7.5);
        }

        var totals = capture.BuildTiming().PhaseTotals;
        Assert.Equal(100, totals["session-create"].TotalMs, precision: 3);
        Assert.Equal(2, totals["session-create"].Count);
        Assert.Equal(1250, totals["provider-bootstrap"].TotalMs, precision: 3);
        Assert.Equal("provider-bootstrap", totals.Keys.First());
        Assert.Equal(["provider-bootstrap", "session-create", "runtime-plan"], totals.Keys);
    }

    [Fact]
    public void RecordPhase_without_active_capture_is_a_no_op()
    {
        ColdStartCapture.RecordPhase("session-create", 10);
        Assert.Empty(new ColdStartCapture().BuildTiming().PhaseTotals);
    }

    [Fact]
    public void RecordPhase_rejects_blank_names()
    {
        Assert.Throws<ArgumentException>(() => ColdStartCapture.RecordPhase(" ", 1));
    }

    [Fact]
    public void Summary_message_includes_the_phase_breakdown()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "vad"))
        {
            ColdStartCapture.RecordPhase("session-create", 1500);
            ColdStartCapture.RecordPhase("session-create", 500);
            ColdStartCapture.RecordPhase("provider-bootstrap", 2400);
            ColdStartCapture.MarkInferenceCompleted("onnx-run");
            ColdStartCapture.MarkFirstStageStart("vad");
        }

        string message = ColdStartCapture.BuildSummaryMessage(capture.BuildTiming());

        Assert.Contains("Phases:", message, StringComparison.Ordinal);
        Assert.Contains("provider-bootstrap=2400 ms", message, StringComparison.Ordinal);
        Assert.Contains("session-create=2000 ms x2", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatPhaseTotals_formats_largest_phases_and_nulls_when_empty()
    {
        Assert.Equal(string.Empty, ColdStartCapture.FormatPhaseTotals(
            new Dictionary<string, PhaseTotal>(StringComparer.Ordinal)));

        string formatted = ColdStartCapture.FormatPhaseTotals(new Dictionary<string, PhaseTotal>(StringComparer.Ordinal)
        {
            ["runtime-plan"] = new PhaseTotal(12.7, 3),
        });
        Assert.Equal(" Phases: runtime-plan=13 ms x3.", formatted);
    }

    [Fact]
    public void BuildTiming_without_marks_reports_null_inference_milestones()
    {
        var capture = new ColdStartCapture();

        PipelineColdStartTiming timing = capture.BuildTiming();

        Assert.Null(timing.FirstInferenceCompletionElapsedMs);
        Assert.Null(timing.FirstInferenceSource);
        Assert.Null(timing.FirstStageStartElapsedMs);
        Assert.Null(timing.FirstSmokeInferenceCompletionElapsedMs);
        Assert.Null(timing.FirstStageInferenceCompletionElapsedMs);
        Assert.Null(timing.FirstUnclassifiedInferenceCompletionElapsedMs);
        Assert.True(timing.RunElapsedMs >= 0);
    }

    [Fact]
    public void Summary_message_reports_the_no_inference_case()
    {
        var capture = new ColdStartCapture();
        string message = ColdStartCapture.BuildSummaryMessage(capture.BuildTiming());

        Assert.Contains("no inference completed", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Summary_message_reports_first_inference_latency()
    {
        var capture = new ColdStartCapture();
        using (ColdStartCapture.Activate(capture))
        using (ColdStartCapture.EnterPhase(ColdStartPhase.Stage, "vad"))
        {
            ColdStartCapture.MarkInferenceCompleted("onnx-run");
            ColdStartCapture.MarkFirstStageStart("vad");
        }

        string message = ColdStartCapture.BuildSummaryMessage(capture.BuildTiming());

        Assert.Contains("first inference completed", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("onnx-run", message, StringComparison.Ordinal);
        Assert.Contains("stage:vad", message, StringComparison.Ordinal);
    }
}
