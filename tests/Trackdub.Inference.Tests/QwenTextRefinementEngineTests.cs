using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.QwenTextRefinement;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Inference.Tests;

public sealed class QwenTextRefinementEngineTests
{
    [RequiresBundledModelFact(
        "Qwen2.5-1.5B-Instruct/genai_config.json",
        "Qwen2.5-1.5B-Instruct/model.onnx",
        "Qwen2.5-1.5B-Instruct/tokenizer.json")]
    public async Task QwenTextRefinementEngine_PolishesBundledModelSegment()
    {
        var engine = new QwenTextRefinementEngine(
            new StubRuntimePlanner(new StageRuntimePlan
            {
                Stage = RuntimeStage.TextRefinement,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = "tonythethompson/Qwen2.5-1.5B-Instruct",
                ModelAlias = "qwen-polisher",
                Variant = "default",
                ExecutionProvider = ExecutionProviderKind.Cpu
            }),
            BenchmarkModelPathResolver.CreateDefault());

        IReadOnlyList<RefinedTextSegment> refined = await engine.RefineAsync(
            new TextRefinementRequest(
                [
                    new TextRefinementInputSegment(
                        0,
                        0.0d,
                        2.5d,
                        "hello world this is a short asr segment")
                ],
                TextRefinementScope.Asr,
                SourceLanguage: "en"),
            CancellationToken.None);

        RefinedTextSegment segment = Assert.Single(refined);
        Assert.Equal(0, segment.Index);
        Assert.Equal("hello world this is a short asr segment", segment.OriginalText);
        Assert.False(string.IsNullOrWhiteSpace(segment.RefinedText));
        Assert.False(string.IsNullOrWhiteSpace(segment.DisplayedText));
        Assert.True(segment.Accepted, $"Polish was rejected: {string.Join(", ", segment.AppliedCorrections)}; output: {segment.RefinedText}");
        Assert.NotEqual(segment.OriginalText, segment.DisplayedText);
        Assert.NotNull(engine.LastExecutionSummary);
        Assert.Equal("cpu", engine.LastExecutionSummary!.SelectedProvider);
        Assert.Equal("qwen-polisher", engine.LastExecutionSummary.ModelAlias);
    }

    private sealed class StubRuntimePlanner(StageRuntimePlan plan) : IRuntimePlanner
    {
        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(plan with { Stage = request.Stage });
    }
}
