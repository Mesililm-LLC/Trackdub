using Trackdub.Application.Dubbing;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Xunit;

namespace Trackdub.Application.Tests.Pipeline;

public class StageWarmupSchedulingTests
{
    private static RuntimeModelSelections Selections() =>
        new(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>());

    private static DubbingSessionOptions Options(
        string? sourceLanguage = "en",
        string targetLanguage = "es",
        bool voiceCloning = false) =>
        new()
        {
            SourceMediaPath = "/tmp/in.wav",
            TargetLanguageCode = targetLanguage,
            SourceLanguageCode = sourceLanguage,
            UseVoiceCloning = voiceCloning,
        };

    [Fact]
    public void StartNextStageWarmup_StartsOnlyFirstWarmableNonDeclinedStage()
    {
        var coordinator = new FakeWarmupCoordinator();
        string[] stages = [StageNames.Vad, StageNames.Export, StageNames.Asr, StageNames.Translation];
        var declined = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { StageNames.Asr };

        DubbingPipelineEngine.PendingStageWarmup? pending =
            DubbingPipelineEngine.StartNextStageWarmup(
                coordinator,
                stages,
                startIndex: 1,
                Selections(),
                Options(voiceCloning: true),
                declined,
                CancellationToken.None);

        Assert.NotNull(pending);
        // Export is unmapped; declined ASR is skipped — translation is the single target.
        Assert.Equal(StageNames.Translation, pending!.StageName);
        Assert.False(pending.Task.IsCompleted); // returned without awaiting
        StageWarmupRequest request = Assert.Single(coordinator.Requests);
        Assert.Equal(RuntimeStage.Translation, request.Stage);
        Assert.Equal("en", request.SourceLanguageCode);
        Assert.Equal("es", request.TargetLanguageCode);
        Assert.True(request.RequiresVoiceClone);
        Assert.NotNull(request.Selections);
    }

    [Fact]
    public void StartNextStageWarmup_NoWarmableRemainder_ReturnsNull()
    {
        var coordinator = new FakeWarmupCoordinator();
        string[] stages = [StageNames.Export, StageNames.PreviewMix];

        Assert.Null(DubbingPipelineEngine.StartNextStageWarmup(
            coordinator, stages, 0, Selections(), Options(), new HashSet<string>(), CancellationToken.None));
        Assert.Empty(coordinator.Requests);
    }

    [Fact]
    public async Task ObserveWarmupAsync_JoinsCompletion_AndSuppressesFailures()
    {
        // Completion: joined, result ignored.
        var completed = Task.FromResult(new StageWarmupResult(Attempted: true, Succeeded: true));
        await DubbingPipelineEngine.ObserveWarmupAsync(completed, CancellationToken.None);

        // Failed optimization result: swallowed.
        var failed = Task.FromResult(new StageWarmupResult(Attempted: true, Succeeded: false, Detail: "boom"));
        await DubbingPipelineEngine.ObserveWarmupAsync(failed, CancellationToken.None);

        // Ordinary exception: optimization must not break the run.
        await DubbingPipelineEngine.ObserveWarmupAsync(
            Task.FromException<StageWarmupResult>(new InvalidOperationException("warmup crashed")),
            CancellationToken.None);
    }

    [Fact]
    public async Task ObserveWarmupAsync_SuppressesCancellation_OnlyWhenRunCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await DubbingPipelineEngine.ObserveWarmupAsync(
            Task.FromCanceled<StageWarmupResult>(cts.Token), cts.Token);

        // Cancellation with a live run token is not ours to swallow.
        using var taskCts = new CancellationTokenSource();
        await taskCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => DubbingPipelineEngine.ObserveWarmupAsync(
                Task.FromCanceled<StageWarmupResult>(taskCts.Token),
                CancellationToken.None));
    }

    [Fact]
    public async Task ObserveWarmupAsync_RunCancellationStopsJoiningPendingWarmup()
    {
        using var cts = new CancellationTokenSource();
        var warmup = new TaskCompletionSource<StageWarmupResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        Task observe = DubbingPipelineEngine.ObserveWarmupAsync(warmup.Task, cts.Token);
        await cts.CancelAsync();

        await observe.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(warmup.Task.IsCompleted);
    }

    [Fact]
    public void ShouldStartConcurrentWarmup_RequiresCapacityAndNoPendingWarmup()
    {
        var singleEntry = new FakeWarmupCoordinator();
        var multiEntry = new FakeWarmupCoordinator { AllowsConcurrent = true };
        var pending = new DubbingPipelineEngine.PendingStageWarmup(
            StageNames.Asr, Task.FromResult(new StageWarmupResult(true, true)));

        Assert.False(DubbingPipelineEngine.ShouldStartConcurrentWarmup(null, null));
        Assert.False(DubbingPipelineEngine.ShouldStartConcurrentWarmup(singleEntry, null));
        Assert.True(DubbingPipelineEngine.ShouldStartConcurrentWarmup(multiEntry, null));
        Assert.False(DubbingPipelineEngine.ShouldStartConcurrentWarmup(multiEntry, pending));
    }

    private sealed class FakeWarmupCoordinator : IStageWarmupCoordinator
    {
        public List<StageWarmupRequest> Requests { get; } = [];

        public bool AllowsConcurrent { get; init; }

        bool IStageWarmupCoordinator.AllowsConcurrentWarmup => AllowsConcurrent;

        public Task<StageWarmupResult> WarmAsync(
            StageWarmupRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return new TaskCompletionSource<StageWarmupResult>().Task; // never completes
        }
    }
}
