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

    private static StageOutcome Succeeded(string stageName) => new()
    {
        StageName = stageName,
        Status = StageStatus.Succeeded,
        StartTime = DateTimeOffset.UtcNow,
        EndTime = DateTimeOffset.UtcNow,
        ArtifactPaths = [],
    };

    [Fact]
    public async Task RunScheduledStagesAsync_NextStageWarmupOverlapsCurrentStageExecution()
    {
        var events = new List<string>();
        void Record(string e) { lock (events) events.Add(e); }
        var coordinator = new ControlledWarmupCoordinator(() => Record("warmup:start"));
        string[] stages = [StageNames.Vad, StageNames.Asr];
        var outcomes = new List<StageOutcome>();
        var vadRecorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task run = DubbingPipelineEngine.RunScheduledStagesAsync(
            stages,
            coordinator,
            Selections(),
            Options(),
            new HashSet<string>(),
            gateStageAsync: (_, _) => Task.FromResult<StageOutcome?>(null),
            executeStageAsync: async (stage, ct) =>
            {
                if (stage == StageNames.Vad)
                {
                    Record("vad:start");
                    // Deadlocks (test-token timeout) unless the ASR warmup was
                    // already started before VAD execution began.
                    await coordinator.Started.Task.WaitAsync(ct).ConfigureAwait(false);
                    Record("vad:end");
                }
                else
                {
                    Record("asr:exec");
                }

                return Succeeded(stage);
            },
            recordOutcome: outcome =>
            {
                outcomes.Add(outcome);
                if (outcome.StageName == StageNames.Vad) vadRecorded.TrySetResult();
            },
            progress: null,
            TestContext.Current.CancellationToken);

        // VAD finished while the ASR warmup is still blocked: ASR must not have run.
        await vadRecorded.Task.WaitAsync(TestContext.Current.CancellationToken);
        lock (events) Assert.DoesNotContain("asr:exec", events);
        Assert.False(coordinator.Release.Task.IsCompleted);

        coordinator.Release.SetResult(new StageWarmupResult(Attempted: true, Succeeded: true));
        await run.WaitAsync(TestContext.Current.CancellationToken);

        string[] snapshot;
        lock (events) snapshot = [.. events];
        Assert.True(Array.IndexOf(snapshot, "warmup:start") < Array.IndexOf(snapshot, "vad:end"),
            "warmup for stage N+1 must start before stage N finishes");
        Assert.Equal([StageNames.Vad, StageNames.Asr], outcomes.Select(o => o.StageName));
        Assert.All(outcomes, o => Assert.Equal(StageStatus.Succeeded, o.Status));
        Assert.Equal(RuntimeStage.Asr, Assert.Single(coordinator.Requests).Stage);
    }

    [Fact]
    public async Task RunScheduledStagesAsync_WarmupFailureSkipsTargetStageWithReason_AndLaterStagesStillRun()
    {
        var coordinator = new DelegateWarmupCoordinator(_ =>
            Task.FromResult(new StageWarmupResult(
                Attempted: true, Succeeded: false, Detail: "provider failed")));
        string[] stages = [StageNames.Vad, StageNames.Asr, StageNames.Translation];
        var outcomes = new List<StageOutcome>();
        var executed = new List<string>();

        await DubbingPipelineEngine.RunScheduledStagesAsync(
            stages,
            coordinator,
            Selections(),
            Options(),
            new HashSet<string>(),
            gateStageAsync: (_, _) => Task.FromResult<StageOutcome?>(null),
            executeStageAsync: (stage, _) =>
            {
                executed.Add(stage);
                return Task.FromResult(Succeeded(stage));
            },
            recordOutcome: outcomes.Add,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal([StageNames.Vad, StageNames.Translation], executed);
        Assert.Equal(3, outcomes.Count);
        Assert.Equal(StageStatus.Succeeded, outcomes[0].Status);
        Assert.Equal(StageStatus.Skipped, outcomes[1].Status);
        Assert.Equal(StageNames.Asr, outcomes[1].StageName);
        Assert.Equal(StageSkipReasonCodes.WarmupFailed, outcomes[1].ReasonCode);
        Assert.Contains("provider failed", outcomes[1].DegradationRecords ?? []);
        Assert.Equal(StageStatus.Succeeded, outcomes[2].Status);
    }

    [Fact]
    public async Task RunScheduledStagesAsync_WarmupFinishingEarly_DoesNotChangeOrderingOrOutcomes()
    {
        var coordinator = new DelegateWarmupCoordinator(_ =>
            Task.FromResult(new StageWarmupResult(Attempted: true, Succeeded: true)));
        string[] stages = [StageNames.Vad, StageNames.Asr];
        var outcomes = new List<StageOutcome>();
        var executed = new List<string>();

        await DubbingPipelineEngine.RunScheduledStagesAsync(
            stages,
            coordinator,
            Selections(),
            Options(),
            new HashSet<string>(),
            gateStageAsync: (_, _) => Task.FromResult<StageOutcome?>(null),
            executeStageAsync: (stage, _) =>
            {
                executed.Add(stage);
                return Task.FromResult(Succeeded(stage));
            },
            recordOutcome: outcomes.Add,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(stages, executed);
        Assert.Equal(stages, outcomes.Select(o => o.StageName));
        Assert.All(outcomes, o => Assert.Equal(StageStatus.Succeeded, o.Status));
    }

    [Fact]
    public void BuildWarmupSkipOutcomeIfFailed_NonAttemptedWarmup_ReturnsNull()
    {
        // Attempted:false means no runnable plan (incl. cloud selections the local
        // planner cannot resolve) — the stage must run and surface its own outcome.
        Assert.Null(DubbingPipelineEngine.BuildWarmupSkipOutcomeIfFailed(
            StageNames.Asr, new StageWarmupResult(Attempted: false, Succeeded: false)));

        StageOutcome? attempted = DubbingPipelineEngine.BuildWarmupSkipOutcomeIfFailed(
            StageNames.Asr, new StageWarmupResult(Attempted: true, Succeeded: false, Detail: "boom"));
        Assert.NotNull(attempted);
        Assert.Equal(StageSkipReasonCodes.WarmupFailed, attempted!.ReasonCode);
    }

    [Fact]
    public async Task RunScheduledStagesAsync_NonAttemptedWarmup_ExecutesTargetStage()
    {
        var coordinator = new DelegateWarmupCoordinator(_ =>
            Task.FromResult(new StageWarmupResult(Attempted: false, Succeeded: false)));
        string[] stages = [StageNames.Vad, StageNames.Asr];
        var outcomes = new List<StageOutcome>();
        var executed = new List<string>();

        await DubbingPipelineEngine.RunScheduledStagesAsync(
            stages, coordinator, Selections(), Options(), new HashSet<string>(),
            gateStageAsync: (_, _) => Task.FromResult<StageOutcome?>(null),
            executeStageAsync: (stage, _) =>
            {
                executed.Add(stage);
                return Task.FromResult(Succeeded(stage));
            },
            recordOutcome: outcomes.Add,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(stages, executed);
        Assert.All(outcomes, o => Assert.Equal(StageStatus.Succeeded, o.Status));
    }

    [Fact]
    public async Task RunScheduledStagesAsync_GateSkipWinsOverFailedWarmup()
    {
        var coordinator = new DelegateWarmupCoordinator(_ =>
            Task.FromResult(new StageWarmupResult(
                Attempted: true, Succeeded: false, Detail: "provider failed")));
        string[] stages = [StageNames.Vad, StageNames.Asr];
        var outcomes = new List<StageOutcome>();
        var executed = new List<string>();

        await DubbingPipelineEngine.RunScheduledStagesAsync(
            stages, coordinator, Selections(), Options(), new HashSet<string>(),
            gateStageAsync: (stage, _) => Task.FromResult<StageOutcome?>(
                stage == StageNames.Asr
                    ? Succeeded(stage) with
                    {
                        Status = StageStatus.Skipped,
                        ReasonCode = StageSkipReasonCodes.ExistingArtifactsValid,
                    }
                    : null),
            executeStageAsync: (stage, _) =>
            {
                executed.Add(stage);
                return Task.FromResult(Succeeded(stage));
            },
            recordOutcome: outcomes.Add,
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal([StageNames.Vad], executed);
        Assert.Equal(StageSkipReasonCodes.ExistingArtifactsValid, outcomes[1].ReasonCode);
    }

    [Fact]
    public void DetermineOverallStatus_PrerequisiteWarmupFailedSkip_ReturnsFailed()
    {
        var outcomes = new List<StageOutcome>
        {
            Succeeded(StageNames.Vad),
            Succeeded(StageNames.Asr) with
            {
                Status = StageStatus.Skipped,
                ReasonCode = StageSkipReasonCodes.WarmupFailed,
            },
            Succeeded(StageNames.Export),
        };

        Assert.Equal(
            DubbingRunStatus.Failed,
            DubbingPipelineEngine.DetermineOverallStatus(outcomes));
        Assert.True(DubbingPipelineEngine.IsBlockingPrerequisiteOutcome(outcomes[1]));
        Assert.False(DubbingPipelineEngine.IsBlockingPrerequisiteOutcome(
            outcomes[1] with { ReasonCode = StageSkipReasonCodes.ExistingArtifactsValid }));
    }

    [Fact]
    public void DetermineOverallStatus_PrerequisiteVadNoRegionsSkip_IsBenign()
    {
        var outcomes = new List<StageOutcome>
        {
            Succeeded(StageNames.Vad),
            Succeeded(StageNames.Asr) with
            {
                Status = StageStatus.Skipped,
                ReasonCode = StageSkipReasonCodes.VadNoRegions,
            },
            Succeeded(StageNames.Export),
        };

        Assert.True(StageSkipReasonCodes.IsBenignSkip(StageSkipReasonCodes.VadNoRegions));
        Assert.False(DubbingPipelineEngine.IsBlockingPrerequisiteOutcome(outcomes[1]));
        Assert.NotEqual(
            DubbingRunStatus.Failed,
            DubbingPipelineEngine.DetermineOverallStatus(outcomes));
    }

    private sealed class ControlledWarmupCoordinator(Action onStarted) : IStageWarmupCoordinator
    {
        bool IStageWarmupCoordinator.AllowsConcurrentWarmup => true;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<StageWarmupResult> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<StageWarmupRequest> Requests { get; } = [];

        public Task<StageWarmupResult> WarmAsync(
            StageWarmupRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            onStarted();
            Started.TrySetResult();
            return Release.Task;
        }
    }

    private sealed class DelegateWarmupCoordinator(
        Func<StageWarmupRequest, Task<StageWarmupResult>> handler) : IStageWarmupCoordinator
    {
        public Task<StageWarmupResult> WarmAsync(
            StageWarmupRequest request,
            CancellationToken cancellationToken = default) => handler(request);
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
