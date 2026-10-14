using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.LipSync;
using Trackdub.Application.LipSynthesis;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Pipeline;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Primary entry point for SDK consumers to execute the dubbing pipeline.
/// Orchestrates validation, pre-flight checks, stage execution, and result aggregation.
/// </summary>
/// <param name="sessionFactory">Factory used to create per-run sessions.</param>
/// <param name="transientFaultBus">
/// Optional shared bus for transient-fault telemetry. When null the engine owns
/// its own bus internally so the <see cref="ITransientFaultReporting"/> surface
/// is always observable. Callers that need shared visibility across engine +
/// diagnostics exporter should DI-register the bus as a singleton.
/// </param>
public sealed class DubbingPipelineEngine(
    IDubbingSessionFactory sessionFactory,
    PipelineTransientFaultBus? transientFaultBus = null) : IDubbingPipelineEngine, ITransientFaultReporting
{
    private readonly IDubbingSessionFactory _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    private readonly PipelineTransientFaultBus _transientFaultBus = transientFaultBus ?? new PipelineTransientFaultBus();

    /// <summary>
    /// The bus instance this engine publishes to. Exposed as <c>internal</c> so the
    /// test/Composition surface can pin identity against the Composition-registered
    /// singleton (spec §4.4 C8 follow-up) without resorting to reflection.
    /// </summary>
    internal PipelineTransientFaultBus TransientFaultBus => _transientFaultBus;

    internal static string? NormalizeAsrSourceLanguageCode(string? sourceLanguageCode) =>
        TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(sourceLanguageCode);

    /// <summary>
    /// Executes the dubbing pipeline according to the provided options.
    /// </summary>
    /// <param name="options">Immutable configuration for this pipeline run.</param>
    /// <param name="progress">Optional progress reporter for stage-level events.</param>
    /// <param name="cancellationToken">Token to request clean abort between stages.</param>
    /// <returns>An immutable <see cref="DubbingRunResult"/> describing the outcome of every stage.</returns>
    public async Task<DubbingRunResult> ExecuteAsync(
        DubbingSessionOptions options,
        IProgress<PipelineProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Guid runId = Guid.NewGuid();
        // All preflight/nested/stage events of this run carry the same RunId and a
        // strictly increasing SequenceNumber; downstream callers see only the wrapper.
        var scopedProgress = new RunScopedProgressReporter(runId, progress);
        DateTimeOffset runStart = DateTimeOffset.UtcNow;
        Guid projectId = Guid.Empty;
        var stageOutcomes = new List<StageOutcome>();
        string[] stagesToRun = ResolveStageOrder(options.StageFilter);

        DubbingRunResult? selectionError = ValidateStageSelection(options, stagesToRun, runId, runStart, stageOutcomes);
        if (selectionError is not null)
        {
            return selectionError;
        }

        bool requiresSourceMedia = stagesToRun
            .Any(stage => DubbingPipelineStages.RequiresSourceMedia(stage));
        (DubbingSessionOptions effectiveOptions, DubbingRunResult? mediaError) =
            await ResolveEffectiveSourceMediaAsync(
                options, requiresSourceMedia, runId, runStart, stageOutcomes, cancellationToken)
                .ConfigureAwait(false);
        if (mediaError is not null)
        {
            return mediaError;
        }

        options = ApplyVoiceCloningDefaults(effectiveOptions);

        // --- Validation: create output directory if missing ---
        string projectOutputDirectory = EnsureProjectDirectory(options);

        // --- Capture immutable ExecutionSnapshot ---
        Dictionary<string, string> executionSnapshot = CaptureExecutionSnapshot(options);

        // --- Create session ---
        (IDubbingSession? session, DubbingRunResult? sessionError) = CreateSessionOrError(
            projectOutputDirectory, options, runId, runStart, stageOutcomes, executionSnapshot);
        if (sessionError is not null || session is null)
        {
            return sessionError!;
        }

        await using (session.ConfigureAwait(false))
        {
            if (RequestsVoiceCloning(options))
            {
                TryResolveService<IConsentService>(session)?.GrantVoiceCloningConsent();
            }

            // Explicit cloud egress consents are applied as a run-scoped overlay via the
            // session settings seeded in CreateSessionOrError. They are never written to the
            // shared consent store, so a later run that omits the option is still gated by
            // what was actually persisted.

            TranscriptProjectState? initialProjectState = null;

            // --- Ensure project/media spine exists for fresh SDK runs ---
            if (requiresSourceMedia)
            {
                using (BenchmarkPhaseCapture.Start("import"))
                    initialProjectState = await EnsureMediaSpineCreatedAsync(session, options, cancellationToken).ConfigureAwait(false);
            }

            (initialProjectState, projectId) = await ResolveTelemetryProjectStateAsync(
                session, initialProjectState, cancellationToken).ConfigureAwait(false);

            if (initialProjectState is not null)
            {
                UpdateExecutionSnapshotWithSourceAudioKind(
                    executionSnapshot,
                    options,
                    ResolveSourceAudioKind(initialProjectState.ProjectState.Artifacts));
            }

            // --- Pre-flight checks ---
            DubbingRunResult? preFlightResult;
            IReadOnlySet<string> declinedOptionalStages;
            using (BenchmarkPhaseCapture.Start("preflight"))
            {
                (preFlightResult, declinedOptionalStages) = await RunPreFlightChecksAsync(
                    session,
                    options,
                    stagesToRun,
                    runId,
                    runStart,
                    stageOutcomes,
                    executionSnapshot,
                    scopedProgress,
                    cancellationToken,
                    initialProjectState).ConfigureAwait(false);
            }
            if (preFlightResult is not null)
            {
                return preFlightResult;
            }

            // Reload selections after pre-flight so starter-pack or settings changes are visible to execution.
            RuntimeModelSelections runtimeSelections = await CreateRuntimeSelectionsAsync(
                session,
                options,
                cancellationToken,
                initialProjectState).ConfigureAwait(false);

            MergeRuntimeModelSelectionsIntoSnapshot(
                executionSnapshot,
                runtimeSelections,
                TryResolveService<IModelAliasResolver>(session));

            // --- Execute stages ---
            await RunStageLoopAsync(
                session,
                options,
                stagesToRun,
                executionSnapshot,
                runtimeSelections,
                scopedProgress,
                projectId,
                runId,
                stageOutcomes,
                declinedOptionalStages,
                cancellationToken).ConfigureAwait(false);

            await RunPostLipSynthesisExportIfNeededAsync(
                session,
                options,
                stagesToRun,
                executionSnapshot,
                runtimeSelections,
                scopedProgress,
                projectId,
                runId,
                stageOutcomes,
                cancellationToken).ConfigureAwait(false);

            // --- Build final result ---
            return BuildCompletedResult(runId, runStart, stageOutcomes, executionSnapshot);
        }
    }

    /// <summary>
    /// Validates the stage filter and target-language selection before any I/O.
    /// Returns an error result when validation fails, otherwise null.
    /// </summary>
    private static DubbingRunResult? ValidateStageSelection(
        DubbingSessionOptions options,
        string[] stagesToRun,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes)
    {
        if (stagesToRun.Length == 0 && options.StageFilter is { Count: > 0 })
        {
            return BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures: [$"StageFilter did not match any known pipeline stage: {string.Join(", ", options.StageFilter)}"]);
        }

        string[] stagesMissingTargetLanguage = stagesToRun
            .Where(DubbingPipelineStages.RequiresTargetLanguage)
            .ToArray();
        if (string.IsNullOrWhiteSpace(options.TargetLanguageCode)
            && stagesMissingTargetLanguage.Length > 0)
        {
            return BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures:
                [$"Target language is required for stage(s): {string.Join(", ", stagesMissingTargetLanguage)}."]);
        }

        return null;
    }

    /// <summary>
    /// Resolves the effective options, falling back to the stored source-media path
    /// from SQLite for existing projects. The media file must exist ONLY if a stage
    /// in this run consumes it. Returns an error result when the media is missing.
    /// </summary>
    private async Task<(DubbingSessionOptions Options, DubbingRunResult? Error)> ResolveEffectiveSourceMediaAsync(
        DubbingSessionOptions options,
        bool requiresSourceMedia,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        CancellationToken cancellationToken)
    {
        // Stages such as Translation, Tts, and Export operate against cached artifacts in
        // the project directory and do not need the original source media file to be present.
        // For existing projects, resolve the stored source path from SQLite before failing.
        DubbingSessionOptions effectiveOptions = options;

        if (requiresSourceMedia
            && !File.Exists(options.SourceMediaPath)
            && options.ProjectOutputDirectory is not null)
        {
            DubbingProjectContext? projectContext = await DubbingProjectContextResolver
                .TryOpenAsync(_sessionFactory, options.ProjectOutputDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(projectContext?.SourceMediaPath))
            {
                effectiveOptions = options with { SourceMediaPath = projectContext.SourceMediaPath };
            }
        }

        if (requiresSourceMedia && !File.Exists(effectiveOptions.SourceMediaPath))
        {
            return (effectiveOptions, BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures: [$"Media file not found: {effectiveOptions.SourceMediaPath}"]));
        }

        return (effectiveOptions, null);
    }

    private static IReadOnlyDictionary<string, bool> BuildExplicitCloudEgressConsents(
        IReadOnlyList<string>? explicitConsents)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (explicitConsents is not null)
        {
            foreach (string key in explicitConsents.Where(static key => !string.IsNullOrWhiteSpace(key)))
            {
                map[key.Trim().ToLowerInvariant()] = true;
            }
        }

        return map;
    }

    private static string EnsureProjectDirectory(DubbingSessionOptions options)
    {
        string projectOutputDirectory = DubbingProjectPaths.ResolveProjectDirectory(
            options.SourceMediaPath,
            options.ProjectOutputDirectory);

        if (!Directory.Exists(projectOutputDirectory))
        {
            Directory.CreateDirectory(projectOutputDirectory);
        }

        return projectOutputDirectory;
    }

    private (IDubbingSession? Session, DubbingRunResult? Error) CreateSessionOrError(
        string projectOutputDirectory,
        DubbingSessionOptions options,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot)
    {
        try
        {
            StudioSettings sessionSettings = StudioSettings.Default with
            {
                DefaultSourceLanguage = options.SourceLanguageCode,
                DefaultTargetLanguage = options.TargetLanguageCode,
                // Explicit per-run TTS timing (CLI/SDK) wins over StudioSettings.Default.
                TtsTiming = options.TtsTiming ?? StudioSettings.Default.TtsTiming,
                // WinML catalog device policy for this run when the host pinned one.
                WindowsMlExecutionDevicePolicy = options.WindowsMlExecutionDevicePolicy
                    ?? StudioSettings.Default.WindowsMlExecutionDevicePolicy,
            };
            return (_sessionFactory.CreateSession(projectOutputDirectory, sessionSettings), null);
        }
        catch (Exception ex)
        {
            return (null, BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures: [$"Failed to create session: {ex.Message}"],
                executionSnapshot: executionSnapshot));
        }
    }

    /// <summary>
    /// Captures the project id for transient-fault telemetry (spec §4.4 follow-up lane V2).
    /// Without this hoist, per-project aggregation silently drops engine-emitted faults whose
    /// ProjectId defaults to Guid.Empty (the bus's CountsByKindForProject rejects Empty).
    /// Tied to the ensure-media-spine path so we don't OpenAsync three times per run.
    /// </summary>
    private static async Task<(TranscriptProjectState? State, Guid ProjectId)> ResolveTelemetryProjectStateAsync(
        IDubbingSession session,
        TranscriptProjectState? initialProjectState,
        CancellationToken cancellationToken)
    {
        try
        {
            if (initialProjectState is not null)
            {
                return (initialProjectState, initialProjectState.ProjectState.Project.Id);
            }

            TranscriptProjectState state = await session.Workspace.Project
                .OpenAsync(cancellationToken).ConfigureAwait(false);
            return (state, state.ProjectState.Project.Id);
        }
        // OperationCanceledException naturally propagates through filter-less try-blocks; no
        // explicit rethrow arm needed. Per AGENTS.md "Unnecessary try/catch blocks. Prefer to remove those."
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort fallback: Snapshot() consumers (DiagnosticsBundleExporter) still receive
            // engine-emitted rows; per-project filter consumers see them as Guid.Empty.
            return (initialProjectState, Guid.Empty);
        }
    }

    /// <summary>
    /// Runs each requested stage in order, honoring cancellation, failed prerequisites,
    /// and artifact resume. Outcomes are appended to <paramref name="stageOutcomes"/>.
    /// </summary>
    private async Task RunStageLoopAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stagesToRun,
        Dictionary<string, string> executionSnapshot,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        Guid projectId,
        Guid runId,
        List<StageOutcome> stageOutcomes,
        IReadOnlySet<string> declinedOptionalStages,
        CancellationToken cancellationToken)
    {
        string? failedPrerequisiteStage = null;

        // Optimization-only lookahead. GenAI currently has a one-entry pool, so the next
        // stage's warmup starts only after the current stage has completed and released its
        // model lease; starting it earlier can evict/reload the active model. At most one
        // pending warmup exists and it is always joined before its target or on exit.
        IStageWarmupCoordinator? warmupCoordinator =
            session.Services?.GetService<IStageWarmupCoordinator>();
        PendingStageWarmup? pendingWarmup = null;
        TtsStreamingPrefetch? ttsPrefetch = null;

        try
        {
            for (int i = 0; i < stagesToRun.Length; i++)
            {
                string stageName = stagesToRun[i];

                if (pendingWarmup is not null
                    && string.Equals(pendingWarmup.StageName, stageName, StringComparison.OrdinalIgnoreCase))
                {
                    await ObserveWarmupAsync(pendingWarmup.Task, cancellationToken).ConfigureAwait(false);
                    pendingWarmup = null;
                }

                // Check cancellation between stages
                if (cancellationToken.IsCancellationRequested)
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, "CANCELLED"));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped, "Cancelled");
                    continue;
                }

                // Skip if a prerequisite stage failed
                if (failedPrerequisiteStage is not null)
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, StageSkipReasonCodes.PrerequisiteFailed));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped,
                        $"Skipped due to failed prerequisite: {failedPrerequisiteStage}");
                    continue;
                }

                // Skip optional stages whose model the user declined during pre-flight
                // provisioning; without the model they would fail mid-run.
                if (declinedOptionalStages.Contains(stageName))
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, StageSkipReasonCodes.OptionalModelDeclined));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped,
                        "Skipped — optional model setup declined");
                    continue;
                }

                // Check resumability: skip stages with valid existing artifacts
                if (!options.ForceRerun &&
                    await HasValidExistingArtifactsAsync(
                        session,
                        options,
                        stageName,
                        executionSnapshot,
                        cancellationToken).ConfigureAwait(false))
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, StageSkipReasonCodes.ExistingArtifactsValid));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped,
                        "Skipped — valid artifacts from prior run");
                    continue;
                }

                if (ttsPrefetch is null &&
                    ShouldOverlapTtsWithTranslation(options, stagesToRun, i, declinedOptionalStages))
                {
                    ttsPrefetch = await TryCreateTtsPrefetchAsync(
                        session,
                        options,
                        runtimeSelections,
                        cancellationToken).ConfigureAwait(false);
                }

                Task<StageOutcome> stageTask = ExecuteStageAsync(
                    session,
                    options,
                    stageName,
                    executionSnapshot,
                    runtimeSelections,
                    progress,
                    projectId,
                    runId,
                    cancellationToken,
                    ttsPrefetch);

                StageOutcome outcome = await stageTask.ConfigureAwait(false);
                stageOutcomes.Add(outcome);

                // Unclaimed clips are useless once TTS has run or translation produced no revision.
                if (ttsPrefetch is not null &&
                    (string.Equals(stageName, StageNames.Tts, StringComparison.OrdinalIgnoreCase) ||
                     (string.Equals(stageName, StageNames.Translation, StringComparison.OrdinalIgnoreCase) &&
                      outcome.Status == StageStatus.Failed)))
                {
                    await ttsPrefetch.DisposeAsync().ConfigureAwait(false);
                    ttsPrefetch = null;
                }

                // Keep the active stage's GenAI residency secure through its actual work.
                // Once released, warm the next stage; its lease is joined at the top of that
                // stage's iteration before execution begins.
                if (warmupCoordinator is not null && pendingWarmup is null)
                {
                    pendingWarmup = StartNextStageWarmup(
                        warmupCoordinator,
                        stagesToRun,
                        i + 1,
                        runtimeSelections,
                        options,
                        declinedOptionalStages,
                        cancellationToken);
                }

                if (outcome.Status == StageStatus.Failed && DubbingPipelineStages.PrerequisiteStages.Contains(stageName))
                {
                    failedPrerequisiteStage = stageName;
                }
            }
        }
        finally
        {
            if (ttsPrefetch is not null)
            {
                await ttsPrefetch.DisposeAsync().ConfigureAwait(false);
            }

            // Stop joining warmup promptly on cancellation; the coordinator receives the
            // same token and will stop at its next cancellable checkpoint.
            if (pendingWarmup is not null)
            {
                await ObserveWarmupAsync(pendingWarmup.Task, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// TTS overlaps translation when segment streaming is on and TTS runs later in this run:
    /// the stage at <paramref name="index"/> must be translation.
    /// </summary>
    internal static bool ShouldOverlapTtsWithTranslation(
        DubbingSessionOptions options,
        string[] stagesToRun,
        int index,
        IReadOnlySet<string> declinedOptionalStages) =>
        options.EnableTranslationSegmentStreaming &&
        string.Equals(stagesToRun[index], StageNames.Translation, StringComparison.OrdinalIgnoreCase) &&
        !declinedOptionalStages.Contains(StageNames.Tts) &&
        stagesToRun.Skip(index + 1).Contains(StageNames.Tts, StringComparer.OrdinalIgnoreCase);

    private static async Task<TtsStreamingPrefetch?> TryCreateTtsPrefetchAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        CancellationToken cancellationToken)
    {
        try
        {
            TranscriptProjectState state = await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (state.TranscriptSegments.Count == 0)
            {
                return null;
            }

            return await session.Workspace.CreateTtsStreamingPrefetchAsync(
                BuildPipelineTtsRequest(state, options, runtimeSelections),
                options.TargetLanguageCode,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Overlap is an optimization: without it the TTS stage synthesizes every segment itself.
            TryResolveService<IApplicationLogger>(session)?.LogWarning(
                "TTS will not overlap translation: the streaming prefetch could not be prepared.", ex);
            return null;
        }
    }

    /// <summary>An in-flight warmup for a named pipeline stage; optimization-only.</summary>
    internal sealed record PendingStageWarmup(string StageName, Task<StageWarmupResult> Task);

    /// <summary>
    /// Starts warmup for the first stage after <paramref name="startIndex"/> that maps to a
    /// runtime stage and was not declined in pre-flight. Returns without awaiting so the
    /// model loads while the current stage executes.
    /// </summary>
    internal static PendingStageWarmup? StartNextStageWarmup(
        IStageWarmupCoordinator coordinator,
        string[] stagesToRun,
        int startIndex,
        RuntimeModelSelections runtimeSelections,
        DubbingSessionOptions options,
        IReadOnlySet<string> declinedOptionalStages,
        CancellationToken cancellationToken)
    {
        for (int i = startIndex; i < stagesToRun.Length; i++)
        {
            if (declinedOptionalStages.Contains(stagesToRun[i])
                || MapStageNameToRuntimeStage(stagesToRun[i]) is not { } runtimeStage)
            {
                continue;
            }

            Task<StageWarmupResult> task;
            try
            {
                task = coordinator.WarmAsync(
                    new StageWarmupRequest(
                        runtimeStage,
                        runtimeSelections,
                        options.SourceLanguageCode,
                        options.TargetLanguageCode,
                        RequestsVoiceCloning(options)),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                task = Task.FromResult(new StageWarmupResult(
                    Attempted: true,
                    Succeeded: false,
                    Detail: ex.Message));
            }

            return new PendingStageWarmup(stagesToRun[i], task);
        }

        return null;
    }

    /// <summary>
    /// Joins a warmup task without letting its outcome affect the run: ordinary failures are
    /// optimization-only, and cancellation is swallowed only when the run itself is cancelled.
    /// </summary>
    internal static async Task ObserveWarmupAsync(
        Task<StageWarmupResult> warmupTask,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await warmupTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Run cancelled — the warmup was optimization-only.
        }
        catch (OperationCanceledException)
        {
            // A cancelled warmup on a live run token is real cancellation, not a failure.
            throw;
        }
        catch (Exception)
        {
            // Optimization failure must not affect stage outcomes.
        }
    }

    /// <summary>
    /// Exports the repaired video after successful lip synthesis, replacing any
    /// earlier mix-producing Export outcome.
    /// </summary>
    private async Task RunPostLipSynthesisExportIfNeededAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stagesToRun,
        Dictionary<string, string> executionSnapshot,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        Guid projectId,
        Guid runId,
        List<StageOutcome> stageOutcomes,
        CancellationToken cancellationToken)
    {
        if (!ShouldRunPostLipSynthesisExport(stagesToRun, stageOutcomes))
        {
            return;
        }

        int priorExportIndex = stageOutcomes.FindLastIndex(static outcome =>
            string.Equals(outcome.StageName, StageNames.Export, StringComparison.OrdinalIgnoreCase));
        if (priorExportIndex >= 0)
        {
            stageOutcomes.RemoveAt(priorExportIndex);
        }

        StageOutcome postLipExportOutcome = await ExecuteStageAsync(
            session,
            options,
            StageNames.Export,
            executionSnapshot,
            runtimeSelections,
            progress,
            projectId,
            runId,
            cancellationToken).ConfigureAwait(false);
        stageOutcomes.Add(postLipExportOutcome);
    }

    private static DubbingRunResult BuildCompletedResult(
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot)
    {
        DubbingRunStatus overallStatus = DetermineOverallStatus(stageOutcomes);
        return new DubbingRunResult
        {
            RunId = runId,
            CorrelationId = runId,
            StartTime = runStart,
            EndTime = DateTimeOffset.UtcNow,
            OverallStatus = overallStatus,
            StageOutcomes = stageOutcomes.AsReadOnly(),
            ExecutionSnapshot = executionSnapshot.AsReadOnly(),
        };
    }

    /// <summary>
    /// Runs pre-flight checks for all stages that will execute.
    /// Uses <see cref="IPipelineReadinessService"/> when available to evaluate all stages
    /// upfront, auto-provision downloadable models before the stage loop, and fail fast
    /// with an aggregated error list instead of discovering failures mid-run.
    /// Falls back to <see cref="IPipelinePreFlightChecker"/> when the readiness service
    /// is not registered (backward-compatible).
    /// </summary>
    private static async Task<(DubbingRunResult? Error, IReadOnlySet<string> DeclinedStages)> RunPreFlightChecksAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stagesToRun,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken,
        TranscriptProjectState? state = null)
    {
        if (session.Workspace.Project is null)
        {
            return (null, EmptyDeclinedStages); // No project yet; pre-flight not applicable.
        }

        RuntimeModelSelections preFlightSelections = await CreateRuntimeSelectionsAsync(
            session,
            options,
            cancellationToken,
            state).ConfigureAwait(false);
        MergeRuntimeModelSelectionsIntoSnapshot(
            executionSnapshot,
            preFlightSelections,
            TryResolveService<IModelAliasResolver>(session));

        // ── New path: IPipelineReadinessService ───────────────────────────────
        IPipelineReadinessService? readinessService = TryResolveService<IPipelineReadinessService>(session);
        RuntimeModelSetupCoordinator? coordinator = TryResolveService<RuntimeModelSetupCoordinator>(session);

        if (readinessService is not null && coordinator is not null)
        {
            return await RunPreFlightWithReadinessServiceAsync(
                session,
                options,
                stagesToRun,
                runId,
                runStart,
                stageOutcomes,
                executionSnapshot,
                preFlightSelections,
                readinessService,
                coordinator,
                progress,
                cancellationToken,
                state)
                .ConfigureAwait(false);
        }

        // ── Legacy fallback: IPipelinePreFlightChecker ────────────────────────
        IPipelinePreFlightChecker? checker = TryResolveService<IPipelinePreFlightChecker>(session);
        if (checker is null)
        {
            return (null, EmptyDeclinedStages);
        }

        return (await RunLegacyModelPreFlightAsync(
            session,
            options,
            stagesToRun,
            runId,
            runStart,
            stageOutcomes,
            executionSnapshot,
            checker,
            cancellationToken).ConfigureAwait(false), EmptyDeclinedStages);
    }

    /// <summary>
    /// Legacy pre-flight path: ensures models per stage via
    /// <see cref="IPipelinePreFlightChecker"/>, deferring auto-downloadable
    /// VAD/ASR/Diarization models to stage execution.
    /// </summary>
    private static async Task<DubbingRunResult?> RunLegacyModelPreFlightAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stagesToRun,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot,
        IPipelinePreFlightChecker checker,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (string stageName in stagesToRun)
        {
            if (ShouldSkipModelPreFlight(stageName, options.ModelPreferences))
                continue;

            if (!options.ForceRerun &&
                await HasValidExistingArtifactsAsync(
                    session, options, stageName, executionSnapshot, cancellationToken).ConfigureAwait(false))
                continue;

            try
            {
                await checker.EnsureModelsAvailableAsync(
                    stageName,
                    cancellationToken,
                    string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase)
                        ? options.SourceLanguageCode
                        : null).ConfigureAwait(false);
            }
            catch (RequiredModelNotAvailableException ex)
            {
                // Legacy: auto-downloadable VAD/ASR/Diar still deferred to stage execution.
                bool deferredStage =
                    string.Equals(stageName, StageNames.Vad, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stageName, StageNames.Diarization, StringComparison.OrdinalIgnoreCase);
                if (!ex.CanAutoDownload || !deferredStage)
                    failures.Add($"{stageName}: {ex.ModelId}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Catch any other unexpected exception during pre-flight model check
                // and record it as a failure for this stage
                failures.Add($"{stageName}: {ex.Message}");
            }
        }

        return failures.Count > 0
            ? BuildErrorResult(
                runId,
                runStart,
                stageOutcomes,
                DubbingRunStatus.PreFlightFailed,
                failures,
                executionSnapshot)
            : null;
    }

    /// <summary>
    /// Optional stages whose runtime degrades in place when the model is absent.
    /// A declined model for these stages is exempt from post-provisioning re-blocking,
    /// and the stage still executes (speech enhancement falls back to FFmpeg/AFX).
    /// </summary>
    private static readonly IReadOnlySet<RuntimeStage> StagesWithOptionalModelFallback =
        new HashSet<RuntimeStage> { RuntimeStage.SpeechEnhancement };

    private static readonly IReadOnlySet<string> EmptyDeclinedStages =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pre-flight using the consolidated readiness gate.
    /// Evaluates all stages upfront, auto-provisions downloadable models before the stage loop,
    /// and fails fast with an aggregated error if anything is still blocking.
    /// </summary>
    private static async Task<(DubbingRunResult? Error, IReadOnlySet<string> DeclinedStages)> RunPreFlightWithReadinessServiceAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stageNames,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot,
        RuntimeModelSelections selections,
        IPipelineReadinessService readinessService,
        RuntimeModelSetupCoordinator coordinator,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken,
        TranscriptProjectState? state = null)
    {
        var declinedStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Map to RuntimeStage, skipping stages with valid existing artifacts (resumable).
        var enabledStages = new List<RuntimeStage>();
        foreach (string stageName in stageNames)
        {
            RuntimeStage? stage = MapStageNameToRuntimeStage(stageName);
            if (stage is null) continue;

            if (!options.ForceRerun &&
                await HasValidExistingArtifactsAsync(
                    session, options, stageName, executionSnapshot, cancellationToken).ConfigureAwait(false))
                continue;

            enabledStages.Add(stage.Value);
        }

        if (enabledStages.Count == 0)
            return (null, EmptyDeclinedStages);

        // Evaluate. The project state is passed so consent gates (voice cloning) are
        // evaluated up front instead of failing mid-run.
        PipelineReadinessReport report = await readinessService
            .EvaluateAsync(
                enabledStages,
                selections,
                state,
                cancellationToken,
                options.SourceLanguageCode,
                options.TargetLanguageCode,
                explicitEgressConsents: BuildExplicitCloudEgressConsents(options.ExplicitCloudEgressConsents))
            .ConfigureAwait(false);

        // Provision downloadable models upfront via the host's interaction surface.
        // Headless sessions auto-download-or-cancel; interactive hosts show dialogs.
        if (report.Stages.Any(s => s.Status == ReadinessState.DownloadRequired))
        {
            RuntimeModelSetupCallbacks setupCallbacks =
                TryResolveService<IPipelineModelSetupInteraction>(session)
                    ?.CreateCallbacks(progress, cancellationToken)
                ?? BuildHeadlessCallbacks(cancellationToken);
            RuntimeModelSetupResult result = await coordinator
                .EnsurePipelineModelsAvailableAsync(
                    session.Workspace,
                    selections,
                    report,
                    setupCallbacks,
                    options.SourceLanguageCode,
                    options.TargetLanguageCode,
                    cancellationToken)
                .ConfigureAwait(false);

            RuntimeModelSetupResult provisionResult = result;

            if (!provisionResult.IsReady)
            {
                return (BuildErrorResult(
                    runId,
                    runStart,
                    stageOutcomes,
                    DubbingRunStatus.PreFlightFailed,
                    ["Model provisioning was cancelled during pre-flight."],
                    executionSnapshot), EmptyDeclinedStages);
            }

            // Stages whose optional model the user declined must not re-block on
            // re-evaluation (the model is still absent). Stages with an in-place
            // fallback still execute and degrade; stages without one are skipped.
            foreach (string stageName in stageNames)
            {
                if (MapStageNameToRuntimeStage(stageName) is not { } skippedStage ||
                    !provisionResult.SkippedStages.Contains(skippedStage))
                {
                    continue;
                }

                enabledStages.Remove(skippedStage);
                if (!StagesWithOptionalModelFallback.Contains(skippedStage))
                {
                    declinedStages.Add(stageName);
                }
            }

            if (enabledStages.Count == 0)
            {
                return (null, declinedStages);
            }

            // Re-evaluate after provisioning with refreshed selections in case settings changed.
            selections = await CreateRuntimeSelectionsAsync(session, options, cancellationToken, state)
                .ConfigureAwait(false);
            MergeRuntimeModelSelectionsIntoSnapshot(
                executionSnapshot,
                selections,
                TryResolveService<IModelAliasResolver>(session));
            readinessService.InvalidateCache(enabledStages);
            report = await readinessService
                .EvaluateAsync(
                    enabledStages,
                    selections,
                    state,
                    cancellationToken,
                    options.SourceLanguageCode,
                    options.TargetLanguageCode,
                    explicitEgressConsents: BuildExplicitCloudEgressConsents(options.ExplicitCloudEgressConsents))
                .ConfigureAwait(false);
        }

        // Collect all remaining blocking states into an aggregated failure list.
        var failures = report.BlockingStages
            .Select(s => FormatPreFlightFailure(s))
            .ToList();

        if (failures.Count > 0)
        {
            return (BuildErrorResult(
                runId,
                runStart,
                stageOutcomes,
                DubbingRunStatus.PreFlightFailed,
                failures,
                executionSnapshot), declinedStages);
        }

        if (enabledStages.Contains(RuntimeStage.Tts) &&
            RequiresCompanionCustomVoiceModel(selections, options, state))
        {
            RuntimeModelSetupCallbacks setupCallbacks =
                TryResolveService<IPipelineModelSetupInteraction>(session)
                    ?.CreateCallbacks(progress, cancellationToken)
                ?? BuildHeadlessCallbacks(cancellationToken);
            RuntimeModelSetupResult companion = await coordinator
                .EnsureTtsModelAvailableAsync(
                    session.Workspace,
                    selections with { TtsModelAlias = Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null) },
                    requiresVoiceClone: false,
                    setupCallbacks,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!companion.IsReady)
            {
                return (BuildErrorResult(
                    runId,
                    runStart,
                    stageOutcomes,
                    DubbingRunStatus.PreFlightFailed,
                    ["Model provisioning was cancelled during pre-flight."],
                    executionSnapshot), declinedStages);
            }
        }

        return (null, declinedStages);
    }

    private static string FormatPreFlightFailure(StageReadiness s) =>
        s.Status switch
        {
            ReadinessState.CloudKeyMissing =>
                $"{s.StageName}: API key not configured for cloud provider" +
                (s.ModelAlias is not null ? $" ({s.ModelAlias})" : string.Empty),
            ReadinessState.ImportRequired =>
                $"{s.StageName}: model requires manual import" +
                (s.ModelId is not null ? $" — {s.ModelId}" : string.Empty),
            ReadinessState.CommercialBlocked =>
                $"{s.StageName}: selected model is non-commercial only",
            ReadinessState.IntegrityFailed =>
                $"{s.StageName}: model checksum verification failed — re-download the model",
            ReadinessState.ConsentRequired =>
                $"{s.StageName}: voice-clone consent required",
            ReadinessState.CloudEgressConsentRequired =>
                $"{s.StageName}: {s.Detail ?? "cloud egress consent required. Set TRACKDUB_CLOUD_EGRESS_CONSENT or use the app to consent interactively."}",
            _ =>
                s.Detail is not null
                    ? $"{s.StageName}: {s.Detail}"
                    : $"{s.StageName}: not ready ({s.Status})",
        };

    /// <summary>
    /// Headless provisioning callbacks: auto-download when possible, cancel otherwise.
    /// No UI dialogs, no file pickers. Used when the session does not register an
    /// <see cref="IPipelineModelSetupInteraction"/> (e.g. minimal test hosts).
    /// </summary>
    private static RuntimeModelSetupCallbacks BuildHeadlessCallbacks(CancellationToken cancellationToken) =>
        HeadlessRuntimeModelSetup.CreateCallbacks(cancellationToken);

    internal static bool ShouldSkipModelPreFlight(
        string stageName,
        IReadOnlyDictionary<string, string>? modelPreferences)
    {
        if (modelPreferences is null || !modelPreferences.TryGetValue(stageName, out string? modelAlias) || string.IsNullOrWhiteSpace(modelAlias))
        {
            return false;
        }

        if (string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase))
        {
            return AsrModelOverrideSettings.IsCloudAlias(modelAlias);
        }

        if (string.Equals(stageName, StageNames.Translation, StringComparison.OrdinalIgnoreCase))
        {
            return TranslationModelOverrideSettings.IsCloudAlias(modelAlias);
        }

        if (string.Equals(stageName, StageNames.Tts, StringComparison.OrdinalIgnoreCase))
        {
            return TtsModelOverrideSettings.IsCloudAlias(modelAlias);
        }

        if (string.Equals(stageName, StageNames.TextRefinementAsr, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stageName, StageNames.TextRefinementTranslation, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(stageName, StageNames.TextRefinement, StringComparison.OrdinalIgnoreCase))
        {
            return modelAlias.StartsWith("gemini", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Ensures the project's media spine (project record + normalized audio) exists.
    /// For fresh SDK runs the project has not been created yet; this creates it from the
    /// source media path without running any transcription stages.
    /// </summary>
    private static async Task<TranscriptProjectState?> EnsureMediaSpineCreatedAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        CancellationToken cancellationToken)
    {
        TranscriptWorkspace workspace = session.Workspace;
        try
        {
            return await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProjectMissingException(ex))
        {
            // Project does not exist yet — create the media spine below.
        }

        string projectName = Path.GetFileNameWithoutExtension(options.SourceMediaPath);
        await workspace.CreateMediaSpineAsync(
            new CreateTranscriptProjectRequest(projectName, options.SourceMediaPath),
            cancellationToken).ConfigureAwait(false);

        return null;
    }

    private static bool RequestsVoiceCloning(DubbingSessionOptions options) =>
        options.UseVoiceCloning ||
        (options.VoiceCloneBySpeakerId?.Values.Any(static clone => clone) ?? false);

    private static bool IsProjectMissingException(Exception ex)
    {
        // ProjectMediaIngestService.OpenAsync throws this exact InvalidOperationException
        // when no project record exists yet.
        if (ex is InvalidOperationException &&
            ex.Message.Contains("does not contain a project record", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // On a brand-new SDK run the SQLite file can exist (created lazily on connection
        // open) before migrations have created the "projects" table — same "fresh project"
        // signal as above, just surfaced lower in the stack.
        if (string.Equals(ex.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal) &&
            ex.Message.Contains("no such table: projects", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Do not widen this further to bare exception types or loose message substrings:
        // those also fire for real failures (DB corruption, permission errors, unrelated
        // schema mismatches) and would silently misroute them into CreateMediaSpineAsync
        // instead of surfacing them.
        return false;
    }

    /// <summary>
    /// Attempts to resolve <see cref="IPipelinePreFlightChecker"/> from the session's DI scope.
    /// </summary>
    private static IPipelinePreFlightChecker? ResolvePreFlightChecker(IDubbingSession session) =>
        TryResolveService<IPipelinePreFlightChecker>(session);

    private static T? TryResolveService<T>(IDubbingSession session) where T : class
    {
        try { return session.Services.GetService<T>(); }
        catch (ObjectDisposedException ex)
        {
            TryLogServiceResolutionFailure<T>(session, ex);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            TryLogServiceResolutionFailure<T>(session, ex);
            return null;
        }
    }

    private static void TryLogServiceResolutionFailure<T>(IDubbingSession session, Exception ex)
    {
        try
        {
            session.Services.GetService<IApplicationLogger>()
                ?.LogWarning(
                    $"Service resolution failed for {typeof(T).Name}; dependent pipeline checks are disabled for this run.",
                    ex);
        }
        catch (Exception)
        {
            // Best-effort logging: swallow any exception from the replaceable logger.
        }
    }

    internal static RuntimeStage? MapStageNameToRuntimeStage(string stageName) =>
        stageName.ToLowerInvariant() switch
        {
            StageNames.Vad => RuntimeStage.Vad,
            StageNames.Asr => RuntimeStage.Asr,
            StageNames.Diarization => RuntimeStage.Diarization,
            StageNames.Translation => RuntimeStage.Translation,
            StageNames.Tts => RuntimeStage.Tts,
            StageNames.Separation => RuntimeStage.Separation,
            StageNames.AudioPreparation => RuntimeStage.SpeechEnhancement,
            StageNames.SpeechEnhancement => RuntimeStage.SpeechEnhancement,
            StageNames.OverlapRescue => RuntimeStage.OverlapRescue,
            StageNames.TextRefinementAsr => RuntimeStage.TextRefinement,
            StageNames.LipSync => RuntimeStage.LipSync,
            StageNames.LipSynthesis => RuntimeStage.LipSynthesis,
            _ => null,
        };

    internal readonly record struct StageWorkflowResult(
        IReadOnlyList<string> ArtifactPaths,
        IReadOnlyList<string>? DegradationRecords,
        StageStatus Status = StageStatus.Succeeded,
        string? ReasonCode = null);

    /// <summary>
    /// Executes a single pipeline stage, wrapping it in timing, transient retry, and error handling.
    /// Transient failures (filesystem locks, SQLite busy, HF mirror 5xx) are retried up to
    /// <see cref="StageRetryBudget.Default"/> attempts with exponential backoff before being
    /// reported as terminal failures.
    /// </summary>
    private async Task<StageOutcome> ExecuteStageAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string stageName,
        Dictionary<string, string> executionSnapshot,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken,
        TtsStreamingPrefetch? ttsPrefetch = null)
    {
        DateTimeOffset stageStart = DateTimeOffset.UtcNow;
        ReportProgress(progress, stageName, PipelineProgressEventKind.Started, null);

        StageRetryBudget retryBudget = StageRetryBudget.Default;
        int attempt = 1;
        while (true)
        {
            try
            {
                StageWorkflowResult workflowResult = await RunStageWorkflowAsync(
                    session,
                    options,
                    stageName,
                    runtimeSelections,
                    progress,
                    cancellationToken,
                    ttsPrefetch).ConfigureAwait(false);

                DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
                PipelineProgressEventKind eventKind = workflowResult.Status switch
                {
                    StageStatus.Skipped => PipelineProgressEventKind.Skipped,
                    StageStatus.Failed => PipelineProgressEventKind.Failed,
                    // PartiallySucceeded reports Completed: the stage finished, and the
                    // degradation detail is carried in the message and outcome records.
                    _ => PipelineProgressEventKind.Completed,
                };
                string? progressMessage = workflowResult.ReasonCode
                    ?? (workflowResult.DegradationRecords is { Count: > 0 } records ? records[0] : null);
                ReportProgress(
                    progress,
                    stageName,
                    eventKind,
                    progressMessage,
                    stageEnd - stageStart);

                return new StageOutcome
                {
                    StageName = stageName,
                    Status = workflowResult.Status,
                    StartTime = stageStart,
                    EndTime = stageEnd,
                    ArtifactPaths = workflowResult.ArtifactPaths,
                    DegradationRecords = workflowResult.DegradationRecords,
                    ReasonCode = workflowResult.ReasonCode,
                };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
                ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, "Cancelled", stageEnd - stageStart);

                return new StageOutcome
                {
                    StageName = stageName,
                    Status = StageStatus.Failed,
                    StartTime = stageStart,
                    EndTime = stageEnd,
                    ArtifactPaths = [],
                    ReasonCode = "CANCELLED",
                };
            }
            catch (Exception ex) when (TransientFailureClassifier.IsTransient(ex))
            {
                TransientFailureKind kind = TransientFailureClassifier.Classify(ex);
                PublishEngineTransient(projectId, stageName, kind, ex, stageStart, attempt, runId);

                if (attempt >= retryBudget.MaxAttempts)
                {
                    DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
                    string retryExhaustedMessage = $"{ex.GetType().Name}: {ex.Message} (transient retry exhausted after {attempt} attempts)";
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, retryExhaustedMessage, stageEnd - stageStart);

                    return new StageOutcome
                    {
                        StageName = stageName,
                        Status = StageStatus.Failed,
                        StartTime = stageStart,
                        EndTime = stageEnd,
                        ArtifactPaths = [],
                        ReasonCode = "STAGE_FAILED_TRANSIENT",
                        DegradationRecords = [retryExhaustedMessage],
                    };
                }

                try
                {
                    await Task.Delay(retryBudget.BackoffFor(attempt), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, "Cancelled during retry backoff", stageEnd - stageStart);

                    return new StageOutcome
                    {
                        StageName = stageName,
                        Status = StageStatus.Failed,
                        StartTime = stageStart,
                        EndTime = stageEnd,
                        ArtifactPaths = [],
                        ReasonCode = "CANCELLED",
                    };
                }
                attempt++;
            }
            catch (Exception ex)
            {
                // Catch all exceptions from stage execution to ensure we always
                // return a StageOutcome rather than letting exceptions propagate.
                // This allows the pipeline to report failures gracefully.
                DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
                ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, ex.Message, stageEnd - stageStart);

                return new StageOutcome
                {
                    StageName = stageName,
                    Status = StageStatus.Failed,
                    StartTime = stageStart,
                    EndTime = stageEnd,
                    ArtifactPaths = [],
                    ReasonCode = "STAGE_FAILED",
                    DegradationRecords = [ex.Message],
                };
            }
        }
    }

    /// <summary>
    /// Dispatches execution to the appropriate workspace workflow method for the given stage.
    /// Returns artifact paths and any degradation records produced by the stage.
    /// </summary>
    internal static async Task<StageWorkflowResult> RunStageWorkflowAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string stageName,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken,
        TtsStreamingPrefetch? ttsPrefetch = null)
    {
        TranscriptWorkspace workspace = session.Workspace;
        InferenceModelPreferences modelPreferences =
            RuntimeModelRequestFactory.CreateModelPreferences(runtimeSelections);

        switch (stageName)
        {
            case StageNames.Separation:
                return await RunSeparationStageAsync(
                    workspace,
                    options,
                    runtimeSelections,
                    modelPreferences,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.Vad:
                return await RunVadStageAsync(
                    workspace,
                    options,
                    modelPreferences,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.Asr:
                return await RunAsrStageAsync(
                    workspace,
                    options,
                    runtimeSelections,
                    modelPreferences,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.Diarization:
                return await RunDiarizationStageAsync(
                    workspace,
                    options,
                    runtimeSelections,
                    modelPreferences,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.AudioPreparation:
                return await RunAudioPreparationStageAsync(
                    workspace,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.TextRefinementAsr:
                return await RunTextRefinementStageAsync(
                    workspace,
                    options,
                    modelPreferences,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.OverlapRescue:
                return await RunOverlapRescueStageAsync(
                    workspace,
                    options,
                    runtimeSelections,
                    modelPreferences,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.Translation:
                return await RunTranslationStageAsync(
                    workspace,
                    options,
                    runtimeSelections,
                    progress,
                    cancellationToken,
                    ttsPrefetch).ConfigureAwait(false);

            case StageNames.Tts:
                return await RunTtsStageAsync(
                    session,
                    options,
                    runtimeSelections,
                    progress,
                    cancellationToken,
                    ttsPrefetch).ConfigureAwait(false);

            case StageNames.Export:
                return await RunExportStageAsync(
                    session,
                    options,
                    progress,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.LipSync:
                return await RunLipSyncStageAsync(
                    workspace,
                    runtimeSelections,
                    cancellationToken).ConfigureAwait(false);

            case StageNames.LipSynthesis:
                return await RunLipSynthesisStageAsync(
                    session,
                    runtimeSelections,
                    cancellationToken).ConfigureAwait(false);

            default:
                // A catalog stage with no registered workflow is a defect, not a no-op;
                // report failure rather than silently succeeding.
                return new StageWorkflowResult(
                    [],
                    [$"Stage '{stageName}' has no registered workflow."],
                    StageStatus.Failed,
                    "STAGE_UNSUPPORTED");
        }
    }

    /// <summary>
    /// Runs stem separation for voice-clone references and the mix bed.
    /// </summary>
    private static async Task<StageWorkflowResult> RunSeparationStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        InferenceModelPreferences modelPreferences,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.EnableStemSeparation)
        {
            return new StageWorkflowResult(
                [],
                ["Stem separation is disabled for this run."],
                StageStatus.Skipped,
                StageSkipReasonCodes.DisabledByOption);
        }

        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState separationState = await workspace.RunStemSeparationAsync(
            cancellationToken,
            preferredModelAlias: runtimeSelections.SeparationModelAlias,
            modelPreferences: modelPreferences).ConfigureAwait(false);

        return BuildStageWorkflowResultFromStageRun(
            separationState,
            StageNames.Separation,
            stageWorkStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunVadStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        InferenceModelPreferences modelPreferences,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        string? normalizedSourceLanguage = NormalizeAsrSourceLanguageCode(options.SourceLanguageCode);
        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState vadState = await workspace.RunTranscriptStageAsync(
            StageNames.Vad,
            enableSpeakerDiarization: false,
            modelPreferences,
            cancellationToken,
            progress,
            sourceLanguage: normalizedSourceLanguage).ConfigureAwait(false);

        // Speech enhancement runs while routing audio for the first transcript stage; surface its
        // failure before the VAD Completed event so it shows in the progress stream and outcome.
        IReadOnlyList<string>? enhancementDegradations =
            ExtractSpeechEnhancementDegradations(vadState, stageWorkStartedUtc);
        if (enhancementDegradations is { Count: > 0 })
        {
            ReportProgress(progress, StageNames.SpeechEnhancement, PipelineProgressEventKind.Failed,
                enhancementDegradations[0]);
        }

        return BuildStageWorkflowResultFromStageRun(vadState, StageNames.Vad, stageWorkStartedUtc, enhancementDegradations);
    }

    private static async Task<StageWorkflowResult> RunAsrStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        InferenceModelPreferences modelPreferences,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState asrOpen = await workspace.Project
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        if (asrOpen.CurrentTranscriptRevision is not null &&
            asrOpen.TranscriptSegments.Count > 0)
        {
            // Interactive re-run: re-transcribe the existing segments in place
            // instead of regenerating a fresh revision from VAD regions.
            IReadOnlyList<Guid> segmentIds = asrOpen.TranscriptSegments
                .Select(static segment => segment.Id)
                .ToArray();
            DateTimeOffset retranscribeStartedUtc = DateTimeOffset.UtcNow;
            TranscriptProjectState retranscribed = await workspace.RetranscribeSegmentsAsync(
                RuntimeModelSetupCoordinator.CreateRetranscribeRequest(
                    runtimeSelections,
                    asrOpen.CurrentTranscriptRevision.Id,
                    segmentIds),
                cancellationToken).ConfigureAwait(false);
            return BuildStageWorkflowResultFromStageRun(retranscribed, StageNames.Asr, retranscribeStartedUtc);
        }

        string? normalizedSourceLanguage = NormalizeAsrSourceLanguageCode(options.SourceLanguageCode);
        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState asrState = await workspace.RunTranscriptStageAsync(
            StageNames.Asr,
            enableSpeakerDiarization: false,
            modelPreferences,
            cancellationToken,
            progress,
            sourceLanguage: normalizedSourceLanguage).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(asrState, StageNames.Asr, stageWorkStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunDiarizationStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        InferenceModelPreferences modelPreferences,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState diarizationOpen = await workspace.Project
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        if (diarizationOpen.CurrentTranscriptRevision is not null &&
            diarizationOpen.TranscriptSegments.Count > 0)
        {
            // Interactive re-run: re-diarize and re-assign speaker ids on the
            // existing transcript segments, matching the desktop host's
            // "Identify speakers" semantics.
            DateTimeOffset rediarizeStartedUtc = DateTimeOffset.UtcNow;
            TranscriptProjectState rediarized = await workspace.RerunDiarizationAsync(
                RuntimeModelSetupCoordinator.CreateRerunDiarizationRequest(runtimeSelections),
                cancellationToken).ConfigureAwait(false);
            return BuildStageWorkflowResultFromStageRun(rediarized, StageNames.Diarization, rediarizeStartedUtc);
        }

        if (!options.EnableSpeakerDiarization)
        {
            // The single-stage diarization path records no run when the option is off
            // (the generation stage only builds a default region plan, which is then
            // discarded). Report the skip explicitly instead of running work whose
            // outcome cannot be observed.
            return new StageWorkflowResult(
                [],
                ["Speaker diarization is disabled for this run."],
                StageStatus.Skipped,
                StageSkipReasonCodes.DisabledByOption);
        }

        string? normalizedSourceLanguage = NormalizeAsrSourceLanguageCode(options.SourceLanguageCode);
        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState diarizationState = await workspace.RunTranscriptStageAsync(
            StageNames.Diarization,
            enableSpeakerDiarization: options.EnableSpeakerDiarization,
            modelPreferences,
            cancellationToken,
            progress,
            sourceLanguage: normalizedSourceLanguage).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(diarizationState, StageNames.Diarization, stageWorkStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunAudioPreparationStageAsync(
        TranscriptWorkspace workspace,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState audioPrepState = await workspace
            .RunSpeechAudioPreparationAsync(cancellationToken, progress)
            .ConfigureAwait(false);
        // A failed speech-enhancement pass inside preparation degrades the cleanup
        // stage even when preparation itself completes on the unenhanced audio.
        return BuildStageWorkflowResultFromStageRun(
            audioPrepState,
            StageNames.AudioPreparation,
            stageWorkStartedUtc,
            ExtractSpeechEnhancementDegradations(audioPrepState, stageWorkStartedUtc));
    }

    private static async Task<StageWorkflowResult> RunTextRefinementStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        InferenceModelPreferences modelPreferences,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        string? normalizedSourceLanguage = NormalizeAsrSourceLanguageCode(options.SourceLanguageCode);
        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState refinementState = await workspace.RunTranscriptStageAsync(
            StageNames.TextRefinementAsr,
            enableSpeakerDiarization: options.EnableSpeakerDiarization,
            modelPreferences,
            cancellationToken,
            progress,
            sourceLanguage: normalizedSourceLanguage).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(refinementState, StageNames.TextRefinementAsr, stageWorkStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunOverlapRescueStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        InferenceModelPreferences modelPreferences,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        TranscriptProjectState preRescueState = await workspace.Project
            .OpenAsync(cancellationToken).ConfigureAwait(false);
        StageRunRecord? diarizationRun = GetLatestStageRun(preRescueState, StageNames.Diarization);
        if (diarizationRun is null ||
            diarizationRun.Status is not (StageRunStatus.Completed
                or StageRunStatus.PartiallyCompleted
                or StageRunStatus.Skipped))
        {
            return new StageWorkflowResult(
                [],
                ["Overlap rescue requires a completed diarization run first."],
                StageStatus.Skipped,
                StageSkipReasonCodes.PrerequisiteFailed);
        }

        var rescueProgress = new Progress<OverlapRescueProgress>(update =>
            ReportProgress(
                progress,
                StageNames.OverlapRescue,
                PipelineProgressEventKind.Progress,
                update.IsPersistingArtifacts
                    ? "Overlap rescue: saving source candidates."
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "Overlap rescue region {0}/{1}",
                        update.CompletedRegions,
                        update.TotalRegions)));

        DateTimeOffset rescueStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState rescueState = await workspace.RunOverlapRescueAsync(
            cancellationToken,
            rescueProgress,
            preferredModelAlias: runtimeSelections.OverlapRescueModelAlias,
            modelPreferences: modelPreferences,
            retranscribeCandidates: options.RetranscribeOverlapCandidates).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(rescueState, StageNames.OverlapRescue, rescueStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunTranslationStageAsync(
        TranscriptWorkspace workspace,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken,
        ITranslatedSegmentObserver? segmentObserver = null)
    {
        TranscriptProjectState translationState = await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (translationState.TranscriptSegments.Count == 0)
        {
            return BuildNoTranscriptSegmentsSkip(StageNames.Translation);
        }

        string sourceLanguage = options.SourceLanguageCode ?? "auto";
        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        RuntimeExecutionProviderSelection translationExecutionProvider =
            RuntimeModelSetupCoordinator.CreateExecutionProviderSelection(
                runtimeSelections,
                RuntimeStage.Translation);
        TranscriptProjectState translatedState = await workspace.GenerateTranslationAsync(
            new GenerateTranslationRequest(
                SourceLanguage: sourceLanguage,
                TargetLanguage: options.TargetLanguageCode,
                PreferredModelAlias: runtimeSelections.TranslationModelAlias,
                PreferredExecutionProvider: translationExecutionProvider.PreferredExecutionProvider,
                RequirePreferredExecutionProvider: translationExecutionProvider.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: RuntimeModelSetupCoordinator.ResolvePreferredModelVariantAlias(
                    runtimeSelections,
                    RuntimeStage.Translation),
                EnableSegmentStreaming: options.EnableTranslationSegmentStreaming),
            cancellationToken,
            progress,
            segmentObserver).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(translatedState, StageNames.Translation, stageWorkStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunTtsStageAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken,
        TtsStreamingPrefetch? ttsPrefetch = null)
    {
        TranscriptWorkspace workspace = session.Workspace;
        TranscriptProjectState ttsState = await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        if (ttsState.TranscriptSegments.Count == 0)
        {
            return BuildNoTranscriptSegmentsSkip(StageNames.Tts);
        }

        if (RequestsVoiceCloning(options))
        {
            TryResolveService<IConsentService>(session)?.GrantVoiceCloningConsent();
            ReportProgress(
                progress,
                StageNames.Tts,
                PipelineProgressEventKind.Progress,
                options.VoiceAssignmentOverrides is { Count: > 0 }
                    ? "Cloning speakers without a voice override from source audio."
                    : "Cloning each speaker from source audio.");
        }

        GenerateTtsForAllSpeakersRequest ttsRequest = BuildPipelineTtsRequest(ttsState, options, runtimeSelections);
        if (ttsRequest.VoiceIdsBySpeakerId is { Count: > 0 })
        {
            ReportProgress(
                progress,
                StageNames.Tts,
                PipelineProgressEventKind.Progress,
                $"Applying voice override to {ttsRequest.VoiceIdsBySpeakerId.Count} speaker(s).");
        }

        if (ttsRequest.FallbackVoiceIdsBySpeakerId is { Count: > 0 })
        {
            ReportProgress(
                progress,
                StageNames.Tts,
                PipelineProgressEventKind.Progress,
                $"Auto-assigning a fallback voice to {ttsRequest.FallbackVoiceIdsBySpeakerId.Count} speaker(s) without a voice assignment (unattended run).");
        }

        DateTimeOffset stageWorkStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState ttsResult = await workspace.GenerateTtsForAllSpeakersAsync(
            ttsRequest,
            cancellationToken,
            progress,
            ttsPrefetch).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(ttsResult, StageNames.Tts, stageWorkStartedUtc);
    }

    private static GenerateTtsForAllSpeakersRequest BuildPipelineTtsRequest(
        TranscriptProjectState state,
        DubbingSessionOptions options,
        RuntimeModelSelections runtimeSelections)
    {
        RuntimeExecutionProviderSelection ttsExecutionProvider =
            RuntimeModelSetupCoordinator.CreateExecutionProviderSelection(
                runtimeSelections,
                RuntimeStage.Tts);
        return BuildUnattendedTtsRequest(
            state,
            options,
            runtimeSelections.TtsModelAlias) with
        {
            PreferredExecutionProvider = ttsExecutionProvider.PreferredExecutionProvider,
            RequirePreferredExecutionProvider = ttsExecutionProvider.RequirePreferredExecutionProvider,
            PreferredModelVariantAlias = RuntimeModelSetupCoordinator.ResolvePreferredModelVariantAlias(
                runtimeSelections,
                RuntimeStage.Tts),
        };
    }

    private static async Task<StageWorkflowResult> RunExportStageAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        TranscriptWorkspace workspace = session.Workspace;
        TranscriptProjectState state = await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        bool hasTranscriptSegments = state.TranscriptSegments.Count > 0;
        ExportOutputContainer container = ResolveExportContainer(options.ExportFormat);
        string outputPath = !string.IsNullOrWhiteSpace(options.ExportOutputPath)
            ? options.ExportOutputPath
            : ResolveExportOutputPath(session.ProjectRootPath, container);
        if (!hasTranscriptSegments)
        {
            ReportProgress(
                progress,
                StageNames.Export,
                PipelineProgressEventKind.Progress,
                "No transcript segments were detected; exporting source media without subtitles or dubbed speech.");
        }

        ExportStageResult exportResult = await workspace.ExportAsync(
            state,
            new ExportStageRequest(
                ProjectId: state.ProjectState.Project.Id,
                OutputPath: outputPath,
                SubtitleFormats: ResolveSubtitleFormats(options.SubtitleFormats, hasTranscriptSegments),
                SubtitleSource: ResolveSubtitleSource(options.SubtitleSource),
                BurnInSubtitles: options.BurnInSubtitles,
                TargetLufs: options.ExportTargetLufs ?? ExportLoudnessTargets.OnlineLufs,
                Container: container,
                SourceGainDb: options.ExportSourceGainDb ?? 0d,
                DubbedSpeechGainDb: options.ExportDubbedSpeechGainDb ?? 0d,
                DuckingGainDb: options.ExportDuckingGainDb,
                ApplyTimbrePolish: options.ApplyTimbrePolish,
                RestoreOriginalPan: options.RestoreOriginalPan,
                MatchOriginalLoudness: options.MatchOriginalLoudness,
                VideoEncoder: options.VideoEncoder,
                // Carry the raw requested formats (including the null/empty distinction)
                // so the export-resume gate persists and compares the same token the
                // snapshot records, instead of the transcript-state-resolved formats.

                RawSubtitleFormats: options.SubtitleFormats),
            cancellationToken).ConfigureAwait(false);
        if (exportResult.IsBlocked)
        {
            throw new InvalidOperationException(exportResult.BlockedReason ?? "Export blocked by tier gate.");
        }

        (StageStatus exportStatus, string? exportReasonCode, IReadOnlyList<string>? exportDegradations) =
            MapStageRunToSdkOutcome(exportResult.StageRun);
        IReadOnlyList<string>? degradations = exportResult.Warnings is { Count: > 0 } warnings
            ? (exportDegradations is { Count: > 0 } existing ? warnings.Concat(existing).ToArray() : warnings)
            : exportDegradations;

        return new StageWorkflowResult(
            [exportResult.OutputPath, exportResult.ExportVideoRelativePath],
            degradations,
            exportStatus,
            exportReasonCode);
    }

    private static async Task<StageWorkflowResult> RunLipSyncStageAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections runtimeSelections,
        CancellationToken cancellationToken)
    {
        if (workspace.LipSync is null)
        {
            throw new InvalidOperationException(
                "Lip-sync workflow is not available in this session. Ensure CompositionRoot registered LipSyncWorkflow.");
        }

        DateTimeOffset lipSyncStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState lipSyncState = await workspace.RunLipSyncAsync(
            new LipSyncAlignAllRequest(
                PreferredModelAlias: runtimeSelections.LipSyncModelAlias),
            cancellationToken).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(lipSyncState, StageNames.LipSync, lipSyncStartedUtc);
    }

    private static async Task<StageWorkflowResult> RunLipSynthesisStageAsync(
        IDubbingSession session,
        RuntimeModelSelections runtimeSelections,
        CancellationToken cancellationToken)
    {
        TranscriptWorkspace workspace = session.Workspace;
        if (workspace.LipSynthesis is null)
        {
            throw new InvalidOperationException(
                "Lip-synthesis workflow is not available in this session. Ensure CompositionRoot registered LipSynthesisWorkflow.");
        }

        (bool isLicenseApproved, bool allowExperimentalExecution) =
            await ResolveLipSynthesisExecutionGatesAsync(
                session,
                runtimeSelections.LipSynthesisModelAlias,
                cancellationToken).ConfigureAwait(false);

        DateTimeOffset lipSynthesisStartedUtc = DateTimeOffset.UtcNow;
        TranscriptProjectState lipSynthesisState = await workspace.RunLipSynthesisAsync(
            new LipSynthesisRunRequest(
                IsLicenseApproved: isLicenseApproved,
                AllowExperimentalExecution: allowExperimentalExecution,
                PreferredModelAlias: runtimeSelections.LipSynthesisModelAlias),
            cancellationToken).ConfigureAwait(false);
        return BuildStageWorkflowResultFromStageRun(lipSynthesisState, StageNames.LipSynthesis, lipSynthesisStartedUtc);
    }

    private static StageWorkflowResult BuildStageWorkflowResultFromStageRun(
        TranscriptProjectState state,
        string stageName,
        DateTimeOffset notBeforeUtc,
        IReadOnlyList<string>? additionalDegradations = null)
    {
        (StageStatus status, string? reasonCode, IReadOnlyList<string>? degradations) =
            MapStageRunToSdkOutcome(GetLatestStageRun(state, stageName, notBeforeUtc));

        if (additionalDegradations is { Count: > 0 })
        {
            degradations = degradations is { Count: > 0 }
                ? additionalDegradations.Concat(degradations).ToArray()
                : additionalDegradations;
            // Degraded output from a nominally successful run is a partial success,
            // not a clean one.
            if (status == StageStatus.Succeeded)
            {
                status = StageStatus.PartiallySucceeded;
            }
        }

        return new StageWorkflowResult([], degradations, status, reasonCode);
    }

    private static StageWorkflowResult BuildNoTranscriptSegmentsSkip(string stageName) =>
        new(
            [],
            ["No transcript segments were detected; this stage has no work to perform."],
            StageStatus.Skipped,
            StageSkipReasonCodes.NoTranscriptSegments);

    /// <summary>
    /// Returns the most recent run for a stage. When <paramref name="notBeforeUtc"/> is
    /// supplied, only runs started at or after that instant qualify — used to bind a
    /// stage outcome to the run produced by the current invocation instead of
    /// reporting a stale row left by an earlier run.
    /// </summary>
    internal static StageRunRecord? GetLatestStageRun(
        TranscriptProjectState state,
        string stageName,
        DateTimeOffset? notBeforeUtc = null) =>
        state.StageRuns
            .Where(r => string.Equals(r.StageName, stageName, StringComparison.OrdinalIgnoreCase)
                && (notBeforeUtc is null || r.StartedAtUtc >= notBeforeUtc))
            .OrderByDescending(static r => r.StartedAtUtc)
            .FirstOrDefault();

    internal static (StageStatus Status, string? ReasonCode, IReadOnlyList<string>? Degradations)
        MapStageRunToSdkOutcome(StageRunRecord? stageRun) =>
        stageRun?.Status switch
        {
            StageRunStatus.Completed =>
                (StageStatus.Succeeded, null, null),
            StageRunStatus.Skipped =>
                (StageStatus.Skipped,
                    stageRun.FailureReason ?? "STAGE_SKIPPED",
                    null),
            StageRunStatus.Failed =>
                (StageStatus.Failed,
                    stageRun.FailureReason ?? "STAGE_FAILED",
                    null),
            StageRunStatus.PartiallyCompleted =>
                (StageStatus.PartiallySucceeded,
                    null,
                    stageRun.FailureReason is not null ? [stageRun.FailureReason] : null),
            StageRunStatus.Canceled =>
                (StageStatus.Failed,
                    stageRun.FailureReason ?? "CANCELLED",
                    null),
            StageRunStatus.Running =>
                (StageStatus.Failed,
                    "STAGE_INCOMPLETE",
                    ["Stage run did not reach a terminal state."]),
            null =>
                (StageStatus.Failed,
                    "STAGE_RUN_MISSING",
                    ["Stage completed without recording a stage run."]),
            _ =>
                (StageStatus.Failed,
                    stageRun.FailureReason ?? "STAGE_FAILED",
                    null),
        };

    // Speech-enhancement runs as an internal sub-step of RunStemSeparationAsync and is caught
    // there (fallback to unenhanced audio). Surface the failure so callers can include it in
    // StageOutcome.DegradationRecords and progress events rather than silently discarding it.
    internal static IReadOnlyList<string>? ExtractSpeechEnhancementDegradations(
        TranscriptProjectState state,
        DateTimeOffset? notBeforeUtc = null)
    {
        // Order first so we evaluate the *most recent* speech-enhancement attempt.
        // Filtering to Failed before ordering would surface stale failures even when
        // a later run succeeded, producing a false degraded outcome after a successful rerun.
        StageRunRecord? latestRun = state.StageRuns
            .Where(r =>
                string.Equals(r.StageName, StageNames.SpeechEnhancement, StringComparison.OrdinalIgnoreCase)
                && (notBeforeUtc is null || r.StartedAtUtc >= notBeforeUtc))
            .OrderByDescending(static r => r.StartedAtUtc)
            .FirstOrDefault();

        if (latestRun is null || latestRun.Status != StageRunStatus.Failed)
        {
            return null;
        }

        string message = string.IsNullOrWhiteSpace(latestRun.FailureReason)
            ? "speech-enhancement failed; falling back to unenhanced audio"
            : $"speech-enhancement failed (falling back to unenhanced audio): {latestRun.FailureReason}";

        return [message];
    }

    /// <summary>
    /// Unattended runs have no voice-assignment step, so speakers without a deliberate
    /// (non-fallback) voice assignment would fail TTS outright. Mirrors the shell's fallback
    /// behavior: picks the first catalog voice whose language matches the dub target language,
    /// ordered by display name like the shell's voice picker. Languages Kokoro does not cover use
    /// the Qwen3 CustomVoice default preset when Qwen3 speaks the language. Speakers stay
    /// unassigned otherwise, so TTS fails with the explicit assignment error instead of dubbing
    /// with a wrong-language voice.
    /// </summary>
    internal static Dictionary<Guid, string>? BuildUnattendedFallbackVoiceIds(
        TranscriptProjectState state,
        string? targetLanguageCode)
    {
        if (state.Speakers.Count == 0)
        {
            return null;
        }

        HashSet<Guid> deliberatelyAssignedSpeakerIds = state.VoiceAssignments
            .Where(static assignment => !assignment.IsFallback)
            .Select(static assignment => assignment.SpeakerId)
            .ToHashSet();

        // Languages Kokoro does not cover use the Qwen3 CustomVoice preset whose native language
        // matches. The picker list can contain every preset for these languages, so take the
        // language default instead of the alphabetically first preset (Aiden is English).
        // Otherwise pick the first matching stock voice.
        string? defaultVoiceId = !string.IsNullOrWhiteSpace(targetLanguageCode) &&
                                 !StockTtsVoiceMatcher.SupportsKokoro(targetLanguageCode)
            ? (Qwen3TtsDefaults.SupportsLanguage(targetLanguageCode)
                ? Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(targetLanguageCode)
                : null)
            : state.AvailableVoices
                .Where(voice => IsVoiceLanguageMatch(voice.LanguageCode, targetLanguageCode))
                .OrderBy(static voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(static voice => voice.VoiceId)
                .FirstOrDefault();
        if (defaultVoiceId is null)
        {
            return null;
        }

        Dictionary<Guid, string> fallbackVoiceIds = state.Speakers
            .Where(speaker => !deliberatelyAssignedSpeakerIds.Contains(speaker.Id))
            .ToDictionary(static speaker => speaker.Id, _ => defaultVoiceId);
        return fallbackVoiceIds.Count > 0 ? fallbackVoiceIds : null;
    }

    /// <summary>
    /// Pins Chatterbox (or an explicit TTS override) when headless voice cloning is requested.
    /// </summary>
    internal static DubbingSessionOptions ApplyVoiceCloningDefaults(DubbingSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!RequestsVoiceCloning(options))
        {
            return options;
        }

        // Callers may hand us an arbitrary IReadOnlyDictionary whose comparer is case-sensitive
        // (e.g. StringComparer.Ordinal), so it can legitimately contain keys that differ only by
        // case such as both "TTS" and "tts". Copying via the collection constructor with an
        // OrdinalIgnoreCase comparer throws ArgumentException on those duplicates, so normalize
        // by explicit enumeration. Duplicate-precedence: last write wins in the source's
        // enumeration order (matches Dictionary enumeration, but that order is not guaranteed).
        var preferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (options.ModelPreferences is not null)
        {
            foreach (var preference in options.ModelPreferences)
            {
                preferences[preference.Key] = preference.Value;
            }
        }

        if (preferences.TryGetValue(StageNames.Tts, out string? explicitTtsAlias))
        {
            // Preserve the explicit TTS override only when it names a clone-capable model.
            // A stock alias (e.g. kokoro-onnx) paired with UseVoiceCloning=true would otherwise
            // reach an incompatible voicepack lookup that throws "Voicepack '...' is not available.";
            // pin the default Chatterbox clone model instead so the clone run stays runnable.
            // Return the case-insensitive copy so BuildModelPreferences' GetValueOrDefault
            // lookup resolves regardless of the casing the caller used for the original TTS key.
            if (VoiceCloningDefaults.IsCloneOnlyModelAlias(explicitTtsAlias))
            {
                return options with { ModelPreferences = preferences };
            }

            preferences[StageNames.Tts] = VoiceCloningDefaults.ResolveDefaultCloneModelAlias(options.TargetLanguageCode);
            return options with { ModelPreferences = preferences };
        }

        preferences[StageNames.Tts] = VoiceCloningDefaults.ResolveDefaultCloneModelAlias(options.TargetLanguageCode);
        return options with { ModelPreferences = preferences };
    }

    /// <summary>
    /// Builds the unattended TTS request: explicit <c>--voice</c> assignments, stock Kokoro
    /// fallbacks for remaining speakers, and per-speaker source-audio cloning when
    /// <see cref="DubbingSessionOptions.UseVoiceCloning"/> is set. Cloning is skipped for
    /// speakers that have a voice override.
    /// </summary>
    internal static GenerateTtsForAllSpeakersRequest BuildUnattendedTtsRequest(
        TranscriptProjectState state,
        DubbingSessionOptions options,
        string? ttsModelAlias)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(options);

        UnattendedVoicePlan voicePlan = ResolveUnattendedVoicePlan(state, options);
        IReadOnlyDictionary<Guid, string> explicitVoiceIds = voicePlan.ExplicitVoiceIds;
        Dictionary<Guid, bool>? cloneBySpeaker = voicePlan.CloneBySpeaker;
        Dictionary<Guid, string>? fallbackVoiceIds = voicePlan.FallbackVoiceIds;

        string? preferredModelAlias = ttsModelAlias;
        if (RequestsVoiceCloning(options) && string.IsNullOrWhiteSpace(preferredModelAlias))
        {
            preferredModelAlias = VoiceCloningDefaults.ResolveDefaultCloneModelAlias(options.TargetLanguageCode);
        }

        return new GenerateTtsForAllSpeakersRequest(
            FallbackVoiceIdsBySpeakerId: fallbackVoiceIds,
            PreferredModelAlias: preferredModelAlias,
            UseReferenceClipForVoiceCloningBySpeakerId: cloneBySpeaker,
            VoiceIdsBySpeakerId: explicitVoiceIds.Count > 0 ? explicitVoiceIds : null);
    }

    /// <summary>
    /// Maps CLI <c>--voice SPEAKER_ID:voice_id</c> keys onto project speakers.
    /// Accepts a speaker Guid, display name (Speaker 1), or Whisper-style <c>SPEAKER_00</c> index.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, string> ResolveVoiceAssignmentOverrides(
        TranscriptProjectState state,
        IReadOnlyDictionary<string, string>? overrides)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (overrides is null || overrides.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        ProjectSpeaker[] speakers =
        [
            .. state.Speakers
                .OrderBy(static speaker => speaker.CreatedAtUtc)
                .ThenBy(static speaker => speaker.Id)
        ];

        // Fresh projects have no speakers yet (diarization hasn't run), and pre-flight
        // resolves the voice plan against that empty state. Index- and name-based keys
        // cannot be validated until speakers exist, so defer matching: pre-flight
        // proceeds on fallback voices and the TTS stage re-resolves — rejecting
        // genuinely bad keys — once speakers are known.
        if (speakers.Length == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var resolved = new Dictionary<Guid, string>();
        foreach ((string key, string voiceId) in overrides)
        {
            if (!TryMatchSpeaker(speakers, key, out ProjectSpeaker? speaker) || speaker is null)
            {
                string known = speakers.Length == 0
                    ? "(none)"
                    : string.Join(", ", speakers.Select(static candidate => candidate.DisplayName));
                throw new InvalidOperationException(
                    $"Voice override '{key}' did not match a speaker. Known speakers: {known}. Use a display name (Speaker 1) or Whisper-style id (SPEAKER_00).");
            }

            resolved[speaker.Id] = voiceId;
        }

        return resolved;
    }

    internal static bool TryMatchSpeaker(
        IReadOnlyList<ProjectSpeaker> speakers,
        string key,
        out ProjectSpeaker? speaker)
    {
        speaker = null;
        if (string.IsNullOrWhiteSpace(key) || speakers.Count == 0)
        {
            return false;
        }

        string trimmed = key.Trim();
        if (Guid.TryParse(trimmed, out Guid speakerId))
        {
            speaker = speakers.FirstOrDefault(candidate => candidate.Id == speakerId);
            return speaker is not null;
        }

        foreach (ProjectSpeaker candidate in speakers)
        {
            if (candidate.DisplayName.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                speaker = candidate;
                return true;
            }
        }

        const string whisperPrefix = "speaker_";
        if (trimmed.StartsWith(whisperPrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(
                trimmed[whisperPrefix.Length..],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int index)
            && index >= 0
            && index < speakers.Count)
        {
            speaker = speakers[index];
            return true;
        }

        return false;
    }

    private static bool IsVoiceLanguageMatch(string voiceLanguageCode, string? targetLanguageCode)
    {
        string? normalizedVoiceLanguage = NormalizeVoiceLanguageCode(voiceLanguageCode);
        string? normalizedTargetLanguage = NormalizeVoiceLanguageCode(targetLanguageCode);
        if (normalizedVoiceLanguage is null || normalizedTargetLanguage is null)
        {
            return true;
        }

        return string.Equals(normalizedVoiceLanguage, normalizedTargetLanguage, StringComparison.Ordinal) ||
               normalizedVoiceLanguage.StartsWith($"{normalizedTargetLanguage}-", StringComparison.Ordinal) ||
               normalizedTargetLanguage.StartsWith($"{normalizedVoiceLanguage}-", StringComparison.Ordinal);
    }

    private static string? NormalizeVoiceLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().Replace('_', '-').ToLowerInvariant();

    private static async Task<RuntimeModelSelections> CreateRuntimeSelectionsAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        CancellationToken cancellationToken,
        TranscriptProjectState? state = null)
    {
        InferenceModelPreferences? preferences = BuildModelPreferences(options);
        RuntimeModelSelections selections;

        if (TryResolveService<IPipelineRuntimeSelectionsProvider>(session) is { } selectionsProvider)
        {
            RuntimeModelSelections? provided = await selectionsProvider
                .CreateSelectionsAsync(state, options, cancellationToken)
                .ConfigureAwait(false);
            if (provided is not null)
            {
                // Explicit per-stage aliases in options.ModelPreferences are caller
                // intent (e.g. the Chatterbox pin ApplyVoiceCloningDefaults installs for
                // clone runs) and take precedence over the host's UI-side selections,
                // matching the precedence the settings path applies via
                // CreateSelectionsFromSettings. ApplyPresetVoiceModelSelection may still
                // retarget TTS when the voices that will speak are Qwen presets, because
                // synthesis will not use a non-CustomVoice model for those voices.
                selections = ApplyModelPreferenceAliases(provided with
                {
                    SelectionIntents = Enum.GetValues<RuntimeStage>().ToDictionary(stage => stage, provided.GetSelectionIntent)
                }, preferences);
                selections = ApplyPresetVoiceModelSelection(selections, options, state);
                return ApplyExecutionProviderPins(selections, options, preferences);
            }
        }

        StudioSettings settings = StudioSettings.Default;
        IServiceProvider? serviceProvider = session.Services;
        if (serviceProvider?.GetService<IStudioSettingsService>() is IStudioSettingsService settingsService)
        {
            settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        selections = RuntimeModelRequestFactory.CreateSelectionsFromSettings(
            settings,
            preferences);
        selections = ApplyPresetVoiceModelSelection(selections, options, state);
        return ApplyExecutionProviderPins(selections, options, preferences);
    }

    /// <summary>
    /// When every voice this run will synthesize is a Qwen3 preset, point preflight at CustomVoice.
    /// Clone runs are left unchanged, because that single alias is the clone model. An alias that
    /// is already CustomVoice is left unchanged, including a larger tier. A mix of preset and stock
    /// voices keeps the stock alias and is provisioned alongside CustomVoice by
    /// <see cref="RequiresCompanionCustomVoiceModel"/>.
    /// </summary>
    internal static RuntimeModelSelections ApplyPresetVoiceModelSelection(
        RuntimeModelSelections selections,
        DubbingSessionOptions options,
        TranscriptProjectState? state)
    {
        if (state is null ||
            RequestsVoiceCloning(options) ||
            Qwen3TtsDefaults.IsCustomVoiceAlias(selections.TtsModelAlias))
        {
            return selections;
        }

        List<string> allVoiceIds = EnumerateUnattendedStockVoiceIds(
            state,
            ResolveUnattendedVoicePlan(state, options),
            options.TargetLanguageCode).ToList();
        return allVoiceIds.Count > 0 && allVoiceIds.All(Qwen3TtsDefaults.IsPresetVoiceId)
            ? selections with
            {
                TtsModelAlias = Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null),
                SelectionIntents = Enum.GetValues<RuntimeStage>().ToDictionary(stage => stage, selections.GetSelectionIntent)
            }
            : selections;
    }

    /// <summary>
    /// True when synthesis will require CustomVoice for at least one speaker while the selected
    /// TTS alias stays a different model. Preflight must provision both.
    /// </summary>
    internal static bool RequiresCompanionCustomVoiceModel(
        RuntimeModelSelections selections,
        DubbingSessionOptions options,
        TranscriptProjectState? state)
    {
        if (state is null ||
            RequestsVoiceCloning(options) ||
            Qwen3TtsDefaults.IsCustomVoiceAlias(selections.TtsModelAlias))
        {
            return false;
        }

        List<string> voiceIds = EnumerateUnattendedStockVoiceIds(
            state,
            ResolveUnattendedVoicePlan(state, options),
            options.TargetLanguageCode).ToList();
        return voiceIds.Any(Qwen3TtsDefaults.IsPresetVoiceId) &&
               voiceIds.Any(static voiceId => !Qwen3TtsDefaults.IsPresetVoiceId(voiceId));
    }

    private readonly record struct UnattendedVoicePlan(
        IReadOnlyDictionary<Guid, string> ExplicitVoiceIds,
        Dictionary<Guid, string>? FallbackVoiceIds,
        Dictionary<Guid, bool>? CloneBySpeaker);

    /// <summary>
    /// Speakers this unattended run will synthesize without cloning, and the voice id each one uses.
    /// Shared by the TTS request and preset preflight so fallback and clone filters cannot drift.
    /// </summary>
    private static UnattendedVoicePlan ResolveUnattendedVoicePlan(
        TranscriptProjectState state,
        DubbingSessionOptions options)
    {
        IReadOnlyDictionary<Guid, string> explicitVoiceIds = ResolveVoiceAssignmentOverrides(
            state,
            options.VoiceAssignmentOverrides);

        Dictionary<Guid, bool>? cloneBySpeaker = null;
        if (options.VoiceCloneBySpeakerId is not null)
        {
            cloneBySpeaker = new Dictionary<Guid, bool>(options.VoiceCloneBySpeakerId);
        }
        else if (options.UseVoiceCloning && state.Speakers.Count > 0)
        {
            cloneBySpeaker = state.Speakers.ToDictionary(
                static speaker => speaker.Id,
                speaker => !explicitVoiceIds.ContainsKey(speaker.Id));
        }

        Dictionary<Guid, string>? fallbackVoiceIds = null;
        if (options.AutoAssignFallbackVoices)
        {
            fallbackVoiceIds = BuildUnattendedFallbackVoiceIds(state, options.TargetLanguageCode);
            if (fallbackVoiceIds is not null)
            {
                fallbackVoiceIds = fallbackVoiceIds
                    .Where(pair => !explicitVoiceIds.ContainsKey(pair.Key))
                    .Where(pair => cloneBySpeaker?.GetValueOrDefault(pair.Key) != true)
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value);
                if (fallbackVoiceIds.Count == 0)
                {
                    fallbackVoiceIds = null;
                }
            }
        }

        return new UnattendedVoicePlan(explicitVoiceIds, fallbackVoiceIds, cloneBySpeaker);
    }

    private static IEnumerable<string> EnumerateUnattendedStockVoiceIds(
        TranscriptProjectState state,
        UnattendedVoicePlan plan,
        string? targetLanguage)
    {
        // GenerateTtsForAllSpeakersAsync skips speakers with no transcript segments, then
        // prefers a request override, then a persisted non-fallback assignment, then the fallback.
        HashSet<Guid> synthesizingSpeakerIds = state.TranscriptSegments
            .Select(static segment => segment.SpeakerId)
            .Where(static speakerId => speakerId is not null)
            .Select(static speakerId => speakerId!.Value)
            .ToHashSet();

        foreach ((Guid speakerId, string voiceId) in plan.ExplicitVoiceIds)
        {
            if (synthesizingSpeakerIds.Contains(speakerId) && !string.IsNullOrWhiteSpace(voiceId))
            {
                yield return voiceId.Trim();
            }
        }

        if (plan.FallbackVoiceIds is not null)
        {
            foreach ((Guid speakerId, string voiceId) in plan.FallbackVoiceIds)
            {
                if (synthesizingSpeakerIds.Contains(speakerId) &&
                    !plan.ExplicitVoiceIds.ContainsKey(speakerId) &&
                    !string.IsNullOrWhiteSpace(voiceId))
                {
                    yield return voiceId.Trim();
                }
            }
        }

        foreach (VoiceAssignment assignment in state.VoiceAssignments)
        {
            if (assignment.IsFallback ||
                !synthesizingSpeakerIds.Contains(assignment.SpeakerId) ||
                plan.ExplicitVoiceIds.ContainsKey(assignment.SpeakerId) ||
                plan.CloneBySpeaker?.GetValueOrDefault(assignment.SpeakerId) == true ||
                plan.FallbackVoiceIds?.ContainsKey(assignment.SpeakerId) == true)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(assignment.VoiceVariant))
            {
                // A prior clone run persists a clone-only model with no voice id. Synthesis
                // substitutes Kokoro where Kokoro speaks, and a Qwen preset where Qwen speaks.
                // Languages neither speaks stay unassigned, matching BuildUnattendedFallbackVoiceIds.
                // KokoroPrimaryAlias is a model alias, not a catalog voice id; it only has to fail
                // IsPresetVoiceId so this speaker is not counted as a Qwen preset.
                if (!VoiceCloningDefaults.IsCloneOnlyModelAlias(assignment.VoiceModelId))
                {
                    continue;
                }

                if (StockTtsVoiceMatcher.SupportsKokoro(targetLanguage))
                {
                    yield return StockTtsDefaults.KokoroPrimaryAlias;
                }
                else if (Qwen3TtsDefaults.SupportsLanguage(targetLanguage))
                {
                    yield return Qwen3TtsDefaults.ResolveDefaultPresetVoiceId(targetLanguage);
                }

                continue;
            }

            yield return assignment.VoiceVariant.Trim();
        }
    }

    /// <summary>
    /// Overlays per-stage execution-provider pins from <paramref name="preferences"/> onto the
    /// resolved <paramref name="selections"/> hardware overrides. Pinned stages become required
    /// providers only when <see cref="DubbingSessionOptions.RequireExecutionProviderPreferences"/>
    /// is set; otherwise they act as preferences and existing host/settings overrides still apply.
    /// </summary>
    private static RuntimeModelSelections ApplyExecutionProviderPins(
        RuntimeModelSelections selections,
        DubbingSessionOptions options,
        InferenceModelPreferences? preferences)
    {
        if (preferences?.PreferredExecutionProviders is not { Count: > 0 })
        {
            return selections;
        }

        // CreateSelectionsFromPreferences maps the stage-keyed provider pins onto the
        // HardwareOverrides key vocabulary (including the ASR engine disambiguation).
        IReadOnlyDictionary<string, ExecutionProviderKind> pinOverrides =
            RuntimeModelRequestFactory.CreateSelectionsFromPreferences(
                preferences,
                selections.AsrModelOverride,
                selections.IsDevBuild).HardwareOverrides;
        var overrides = new Dictionary<string, ExecutionProviderKind>(
            selections.HardwareOverrides,
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ExecutionProviderKind provider) in pinOverrides)
        {
            overrides[key] = provider;
        }

        return selections with
        {
            HardwareOverrides = overrides,
            RequirePreferredExecutionProviders = selections.RequirePreferredExecutionProviders
                || options.RequireExecutionProviderPreferences,
        };
    }

    /// <summary>
    /// Overlays non-null per-stage model aliases from explicit
    /// <see cref="InferenceModelPreferences"/> onto a host-provided selection set.
    /// Fields the request did not pin keep the host's selection.
    /// </summary>
    internal static RuntimeModelSelections ApplyModelPreferenceAliases(
        RuntimeModelSelections selections,
        InferenceModelPreferences? preferences)
    {
        if (preferences is null)
        {
            return selections;
        }

        RuntimeModelSelections requested = RuntimeModelRequestFactory.CreateSelectionsFromPreferences(preferences);
        return selections with
        {
            SelectionIntents = Enum.GetValues<RuntimeStage>().ToDictionary(stage => stage,
                stage => requested.GetSelectionIntent(stage) == RuntimeModelSelectionIntent.Explicit
                    ? RuntimeModelSelectionIntent.Explicit : selections.GetSelectionIntent(stage)),
            VadModelAlias = preferences.VadModelAlias ?? selections.VadModelAlias,
            DiarizationModelAlias = preferences.DiarizationModelAlias ?? selections.DiarizationModelAlias,
            SeparationModelAlias = preferences.SeparationModelAlias ?? selections.SeparationModelAlias,
            OverlapRescueModelAlias = preferences.OverlapRescueModelAlias ?? selections.OverlapRescueModelAlias,
            AsrModelAlias = preferences.AsrModelAlias ?? selections.AsrModelAlias,
            TranslationModelAlias = preferences.TranslationModelAlias ?? selections.TranslationModelAlias,
            TtsModelAlias = preferences.TtsModelAlias ?? selections.TtsModelAlias,
            TextRefinementModelAlias = preferences.TextRefinementModelAlias ?? selections.TextRefinementModelAlias,
            LipSyncModelAlias = preferences.LipSyncModelAlias ?? selections.LipSyncModelAlias,
            LipSynthesisModelAlias = preferences.LipSynthesisModelAlias ?? selections.LipSynthesisModelAlias,
        };
    }

    /// <summary>
    /// Builds <see cref="InferenceModelPreferences"/> from the dubbing session options.
    /// Per-stage provider pins in <see cref="DubbingSessionOptions.ExecutionProviderPreferences"/>
    /// are mapped from canonical <see cref="StageNames"/> keys onto runtime stages and marked
    /// required; a stage name with no runtime mapping or an unparseable provider label is an
    /// argument error, never silently ignored.
    /// </summary>
    internal static InferenceModelPreferences? BuildModelPreferences(DubbingSessionOptions options)
    {
        bool hasModelOverrides = options.ModelPreferences is { Count: > 0 };
        bool hasProviderPins = options.ExecutionProviderPreferences is { Count: > 0 };
        if (!hasModelOverrides && !hasProviderPins && !options.EnableAsrTextRefinement)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> modelPreferences = options.ModelPreferences
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<RuntimeStage, ExecutionProviderKind>? providerPins =
            ParseExecutionProviderPreferences(options);

        return new InferenceModelPreferences(
            VadModelAlias: modelPreferences.GetValueOrDefault(StageNames.Vad),
            AsrModelAlias: modelPreferences.GetValueOrDefault(StageNames.Asr),
            DiarizationModelAlias: modelPreferences.GetValueOrDefault(StageNames.Diarization),
            SeparationModelAlias: modelPreferences.GetValueOrDefault(StageNames.Separation),
            OverlapRescueModelAlias: modelPreferences.GetValueOrDefault(StageNames.OverlapRescue),
            TranslationModelAlias: modelPreferences.GetValueOrDefault(StageNames.Translation),
            TtsModelAlias: modelPreferences.GetValueOrDefault(StageNames.Tts),
            TextRefinementModelAlias: modelPreferences.GetValueOrDefault(StageNames.TextRefinementAsr),
            LipSyncModelAlias: modelPreferences.GetValueOrDefault(StageNames.LipSync),
            LipSynthesisModelAlias: modelPreferences.GetValueOrDefault(StageNames.LipSynthesis),
            EnableAsrTextRefinement: options.EnableAsrTextRefinement,
            PreferredExecutionProviders: providerPins,
            RequiredExecutionProviderStages:
                providerPins is { Count: > 0 } && options.RequireExecutionProviderPreferences
                    ? new HashSet<RuntimeStage>(providerPins.Keys)
                    : null);
    }

    /// <summary>
    /// Parses <see cref="DubbingSessionOptions.ExecutionProviderPreferences"/> into a
    /// runtime-stage keyed map. Returns null when no pins were supplied.
    /// </summary>
    private static IReadOnlyDictionary<RuntimeStage, ExecutionProviderKind>? ParseExecutionProviderPreferences(
        DubbingSessionOptions options)
    {
        if (options.ExecutionProviderPreferences is not { Count: > 0 } pins)
        {
            return null;
        }

        var providers = new Dictionary<RuntimeStage, ExecutionProviderKind>();
        foreach ((string stageName, string label) in pins)
        {
            if (MapStageNameToRuntimeStage(stageName) is not { } stage)
            {
                throw new ArgumentException(
                    $"Execution provider pin names unknown or non-runtime stage '{stageName}'.",
                    nameof(options));
            }

            if (!ExecutionProviderTokens.TryParse(label, out ExecutionProviderKind provider))
            {
                throw new ArgumentException(
                    $"Execution provider pin for stage '{stageName}' has invalid provider label '{label}'. " +
                    $"Expected one of: {ExecutionProviderTokens.FormatSupportedCliTags()}.",
                    nameof(options));
            }

            providers[stage] = ExecutionProviderTokens.ResolvePlatformPin(provider);
        }

        return providers;
    }

    /// <summary>
    /// Merges resolved runtime model aliases (settings, starter packs, explicit preferences)
    /// into the execution snapshot so artifact resume checks compare against models that
    /// will actually execute.
    /// </summary>
    internal static void MergeRuntimeModelSelectionsIntoSnapshot(
        Dictionary<string, string> snapshot,
        RuntimeModelSelections selections,
        IModelAliasResolver? modelAliasResolver = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selections);

        RuntimeModelRequestOptions options = RuntimeModelRequestFactory.CreateOptions(selections);

        SetSnapshotModelAlias(snapshot, StageNames.Asr, selections.AsrModelAlias);
        SetSnapshotModelVariant(snapshot, options, StageNames.Asr, RuntimeStage.Asr, selections.AsrModelAlias);
        AddModelId(snapshot, StageNames.Asr, selections.AsrModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Diarization, selections.DiarizationModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.Diarization,
            RuntimeStage.Diarization,
            selections.DiarizationModelAlias);
        AddModelId(snapshot, StageNames.Diarization, selections.DiarizationModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Separation, selections.SeparationModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.Separation,
            RuntimeStage.Separation,
            selections.SeparationModelAlias);
        AddModelId(snapshot, StageNames.Separation, selections.SeparationModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.OverlapRescue, selections.OverlapRescueModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.OverlapRescue,
            RuntimeStage.OverlapRescue,
            selections.OverlapRescueModelAlias);
        AddModelId(snapshot, StageNames.OverlapRescue, selections.OverlapRescueModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Translation, selections.TranslationModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.Translation,
            RuntimeStage.Translation,
            selections.TranslationModelAlias);
        AddModelId(snapshot, StageNames.Translation, selections.TranslationModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Tts, selections.TtsModelAlias);
        SetSnapshotModelVariant(snapshot, options, StageNames.Tts, RuntimeStage.Tts, selections.TtsModelAlias);
        AddModelId(snapshot, StageNames.Tts, selections.TtsModelAlias, modelAliasResolver);

        if (selections.EnableAsrTextRefinement)
        {
            SetSnapshotModelAlias(snapshot, StageNames.TextRefinementAsr, selections.TextRefinementModelAlias);
            SetSnapshotModelVariant(
                snapshot,
                options,
                StageNames.TextRefinementAsr,
                RuntimeStage.TextRefinement,
                selections.TextRefinementModelAlias);
            AddModelId(snapshot, StageNames.TextRefinementAsr, selections.TextRefinementModelAlias, modelAliasResolver);
        }

        SetSnapshotModelAlias(snapshot, StageNames.LipSync, selections.LipSyncModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.LipSync,
            RuntimeStage.LipSync,
            selections.LipSyncModelAlias);
        AddModelId(snapshot, StageNames.LipSync, selections.LipSyncModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.LipSynthesis, selections.LipSynthesisModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.LipSynthesis,
            RuntimeStage.LipSynthesis,
            selections.LipSynthesisModelAlias);
        AddModelId(snapshot, StageNames.LipSynthesis, selections.LipSynthesisModelAlias, modelAliasResolver);

        // Hardware/provider overrides change which execution provider a stage runs on; a
        // prior run's artifacts were produced under the provider recorded on its stage row,
        // so resume must compare them rather than reuse artifacts across provider changes.
        foreach ((string stageName, RuntimeStage stage) in ProviderSnapshotStages)
        {
            SetSnapshotExecutionProvider(snapshot, options, stageName, stage);
        }
    }

    private static readonly (string StageName, RuntimeStage Stage)[] ProviderSnapshotStages =
    [
        (StageNames.Vad, RuntimeStage.Vad),
        (StageNames.Asr, RuntimeStage.Asr),
        (StageNames.Diarization, RuntimeStage.Diarization),
        (StageNames.Separation, RuntimeStage.Separation),
        (StageNames.OverlapRescue, RuntimeStage.OverlapRescue),
        (StageNames.Translation, RuntimeStage.Translation),
        (StageNames.Tts, RuntimeStage.Tts),
        (StageNames.AudioPreparation, RuntimeStage.SpeechEnhancement),
        (StageNames.TextRefinementAsr, RuntimeStage.TextRefinement),
        (StageNames.LipSync, RuntimeStage.LipSync),
        (StageNames.LipSynthesis, RuntimeStage.LipSynthesis),
    ];

    private static void SetSnapshotExecutionProvider(
        Dictionary<string, string> snapshot,
        RuntimeModelRequestOptions options,
        string stageName,
        RuntimeStage stage)
    {
        ExecutionProviderKind? preferredProvider =
            RuntimeModelRequestFactory.ResolvePreferredExecutionProvider(options, stage);
        if (preferredProvider is not null)
        {
            snapshot[$"Provider:{stageName}"] =
                RuntimeModelRequestFactory.FormatExecutionProviderLabel(preferredProvider.Value);
        }
    }

    private static void AddModelId(
        Dictionary<string, string> snapshot,
        string stageName,
        string? modelAlias,
        IModelAliasResolver? modelAliasResolver)
    {
        if (modelAliasResolver is not null &&
            !string.IsNullOrWhiteSpace(modelAlias) &&
            modelAliasResolver.TryResolveModelId(modelAlias, out string? modelId) &&
            !string.IsNullOrWhiteSpace(modelId))
        {
            snapshot[$"ModelId:{stageName}"] = modelId;
        }
    }

    private static void SetSnapshotModelAlias(
        Dictionary<string, string> snapshot,
        string stageName,
        string? modelAlias)
    {
        if (!string.IsNullOrWhiteSpace(modelAlias))
        {
            snapshot[$"Model:{stageName}"] = modelAlias;
        }
    }

    private static void SetSnapshotModelVariant(
        Dictionary<string, string> snapshot,
        RuntimeModelRequestOptions options,
        string stageName,
        RuntimeStage stage,
        string? modelAlias)
    {
        string? variant = RuntimeModelRequestFactory.ResolvePreferredModelVariantAlias(options, stage, modelAlias);
        if (!string.IsNullOrWhiteSpace(variant))
        {
            snapshot[$"ModelVariant:{stageName}"] = variant;
        }
    }

    /// <summary>
    /// Captures an immutable snapshot of provider/model/voice decisions at run start.
    /// </summary>
    internal static Dictionary<string, string> CaptureExecutionSnapshot(DubbingSessionOptions options)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceMediaPath"] = options.SourceMediaPath,
            ["TargetLanguageCode"] = options.TargetLanguageCode,
            ["ForceRerun"] = options.ForceRerun.ToString(),
            ["EnableAsrTextRefinement"] = options.EnableAsrTextRefinement.ToString(),
            ["UseVoiceCloning"] = options.UseVoiceCloning.ToString(),
        };

        if (options.TtsTiming is not null)
        {
            snapshot["TtsTiming.EnableRubberbandStretch"] = options.TtsTiming.EnableRubberbandStretch.ToString();
            snapshot["TtsTiming.RubberbandStretchThreshold"] =
                options.TtsTiming.RubberbandStretchThreshold.ToString("G17", CultureInfo.InvariantCulture);
            if (options.TtsTiming.AutoStretchMaxOverrun is double autoStretchMaxOverrun)
            {
                snapshot["TtsTiming.AutoStretchMaxOverrun"] =
                    autoStretchMaxOverrun.ToString("G17", CultureInfo.InvariantCulture);
            }
        }

        snapshot[TtsTimingResumeIdentity.SnapshotKey] =
            TtsTimingResumeIdentity.Normalize(options.TtsTiming?.AutoStretchMaxOverrun);

        // Audio/subtitle/encoder flags (and the pre-existing ExportFormat) gate the Export
        // stage's artifact resume: any change here must invalidate a cached export so it reruns
        // without requiring --force-rerun. These values are produced by ExportResumeGating so
        // capture (here) and comparison (the persisted ExportManifest, read back by
        // StageArtifactResumeEvaluator) share one normalization and cannot drift.
        // Note: sourceAudioKind defaults to NormalizedAudio here because the project may not exist
        // yet. The actual kind is determined at export time when BuildExportGating inspects artifacts.
        foreach ((string key, string value) in ExportResumeGating.Build(
            ResolveExportContainer(options.ExportFormat),
            ArtifactKind.NormalizedAudio,
            options.ApplyTimbrePolish,
            options.RestoreOriginalPan,
            options.MatchOriginalLoudness,
            options.BurnInSubtitles,
            ResolveSubtitleSource(options.SubtitleSource),
            ExportResumeGating.SubtitleFormatsTokenFromRawOptions(options.SubtitleFormats),
            options.VideoEncoder,
            options.ExportTargetLufs ?? ExportLoudnessTargets.OnlineLufs,
            options.ExportSourceGainDb ?? 0d,
            options.ExportDubbedSpeechGainDb ?? 0d,
            options.ExportDuckingGainDb))
        {
            snapshot[key] = value;
        }

        if (options.SourceLanguageCode is not null)
        {
            snapshot["SourceLanguage"] = options.SourceLanguageCode;
        }

        if (options.ModelPreferences is not null)
        {
            foreach ((string stage, string model) in options.ModelPreferences)
            {
                snapshot[$"Model:{stage}"] = model;
            }
        }

        if (options.VoiceCloneBySpeakerId is not null)
        {
            foreach ((Guid speaker, bool clone) in options.VoiceCloneBySpeakerId)
            {
                snapshot[$"VoiceClone:{speaker:D}"] = clone.ToString();
            }
        }

        if (options.VoiceAssignmentOverrides is not null)
        {
            foreach ((string speaker, string voice) in options.VoiceAssignmentOverrides)
            {
                snapshot[$"Voice:{speaker}"] = voice;
            }
        }

        return snapshot;
    }

    private static ArtifactKind ResolveSourceAudioKind(IReadOnlyList<ProjectArtifact> artifacts) =>
        TranscriptWorkflowUtilities.GetLatestAcceptedAmbianceStem(artifacts) is not null
            ? ArtifactKind.Ambiance
            : ArtifactKind.NormalizedAudio;

    private static void UpdateExecutionSnapshotWithSourceAudioKind(
        Dictionary<string, string> snapshot,
        DubbingSessionOptions options,
        ArtifactKind sourceAudioKind)
    {
        foreach ((string key, string value) in ExportResumeGating.Build(
            ResolveExportContainer(options.ExportFormat),
            sourceAudioKind,
            options.ApplyTimbrePolish,
            options.RestoreOriginalPan,
            options.MatchOriginalLoudness,
            options.BurnInSubtitles,
            ResolveSubtitleSource(options.SubtitleSource),
            ExportResumeGating.SubtitleFormatsTokenFromRawOptions(options.SubtitleFormats),
            options.VideoEncoder,
            options.ExportTargetLufs ?? ExportLoudnessTargets.OnlineLufs,
            options.ExportSourceGainDb ?? 0d,
            options.ExportDubbedSpeechGainDb ?? 0d,
            options.ExportDuckingGainDb))
        {
            snapshot[key] = value;
        }
    }

    internal static ExportOutputContainer ResolveExportContainer(string? exportFormat) =>
        ExportStageRequestBuilder.ResolveContainer(exportFormat);

    internal static string ResolveExportOutputPath(string projectRootPath, ExportOutputContainer container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        string extension = container == ExportOutputContainer.Mkv ? ".mkv" : ".mp4";
        return Path.Join(projectRootPath, "exports", "dubbed" + extension);
    }

    private static IReadOnlyList<ExportSubtitleFormat> ResolveSubtitleFormats(
        IReadOnlyList<string>? formats, bool hasTranscriptSegments)
    {
        if (formats is null)
            return hasTranscriptSegments ? [ExportSubtitleFormat.Srt] : [];
        var result = new List<ExportSubtitleFormat>(formats.Count);
        foreach (string? f in formats)
        {
            if (string.IsNullOrWhiteSpace(f))
            {
                continue;
            }

            if (f.Equals("srt", StringComparison.OrdinalIgnoreCase)) result.Add(ExportSubtitleFormat.Srt);
            else if (f.Equals("vtt", StringComparison.OrdinalIgnoreCase)) result.Add(ExportSubtitleFormat.Vtt);
            else if (f.Equals("ass", StringComparison.OrdinalIgnoreCase)) result.Add(ExportSubtitleFormat.Ass);
        }
        return result;
    }

    private static ExportSubtitleSource ResolveSubtitleSource(string? source) =>
        source?.Trim().ToLowerInvariant() switch
        {
            "transcript" => ExportSubtitleSource.Transcript,
            "bilingual" => ExportSubtitleSource.Bilingual,
            _ => ExportSubtitleSource.Translated,
        };

    /// <summary>
    /// Determines the ordered list of stages to execute, respecting an optional filter.
    /// Unfiltered runs use the speech/export default order. Filtered runs resolve against
    /// the extended stage order so lip-sync / lip-synthesis can be opted in.
    /// </summary>
    internal static string[] ResolveStageOrder(IReadOnlyList<string>? stageFilter)
    {
        if (stageFilter is null || stageFilter.Count == 0)
        {
            return [.. DubbingPipelineStages.DefaultStageOrder];
        }

        HashSet<string> filterSet = new(stageFilter, StringComparer.OrdinalIgnoreCase);
        return DubbingPipelineStages.ExtendedStageOrder.Where(s => filterSet.Contains(s)).ToArray();
    }

    /// <summary>
    /// Checks whether a stage has valid existing artifacts from a prior run
    /// that match the current execution snapshot.
    /// </summary>
    private static async Task<bool> HasValidExistingArtifactsAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string stageName,
        Dictionary<string, string> currentSnapshot,
        CancellationToken cancellationToken)
    {
        IArtifactStore? artifactStore = session.Services.GetService<IArtifactStore>();
        if (artifactStore is null)
        {
            return false;
        }

        TranscriptProjectState state;
        try
        {
            state = await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
        {
            return false;
        }

        // The resume check must look at the path the next run would actually write to:
        // an explicit ExportOutputPath takes precedence over the project-default exports/
        // location, and only that effective path proves the prior export is still usable.
        // Path.GetFullPath mirrors ExportStageHandler's normalization (relative explicit
        // paths resolve against the current directory, same as at execution time).
        string? exportPath = null;
        if (string.Equals(stageName, StageNames.Export, StringComparison.OrdinalIgnoreCase))
        {
            string effectiveOutputPath = string.IsNullOrWhiteSpace(options.ExportOutputPath)
                ? ResolveExportOutputPath(
                    session.ProjectRootPath,
                    ResolveExportContainer(options.ExportFormat))
                : options.ExportOutputPath;
            try
            {
                exportPath = Path.GetFullPath(effectiveOutputPath);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                exportPath = effectiveOutputPath;
            }
        }

        return await StageArtifactResumeEvaluator.CanResumeStageAsync(
            state,
            artifactStore,
            stageName,
            currentSnapshot,
            session.ProjectRootPath,
            options.TargetLanguageCode,
            exportPath,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Skip reason codes that represent intentional resume/prerequisite gating rather than
    /// a failed attempt to run a requested stage.
    /// </summary>
    internal static bool IsBenignSkipReasonCode(string? reasonCode) =>
        StageSkipReasonCodes.IsBenignSkip(reasonCode);

    internal static bool ShouldRunPostLipSynthesisExport(
        IReadOnlyList<string> stagesToRun,
        IReadOnlyList<StageOutcome> outcomes)
    {
        if (!stagesToRun.Any(static stage =>
                string.Equals(stage, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        StageOutcome? lipSynthesisOutcome = outcomes.LastOrDefault(static outcome =>
            string.Equals(outcome.StageName, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase));

        return lipSynthesisOutcome?.Status
            is StageStatus.Succeeded or StageStatus.PartiallySucceeded;
    }

    private static async Task<(bool IsLicenseApproved, bool AllowExperimentalExecution)>
        ResolveLipSynthesisExecutionGatesAsync(
            IDubbingSession session,
            string? preferredModelAlias,
            CancellationToken cancellationToken)
    {
        IModelInventoryService? inventoryService = TryResolveService<IModelInventoryService>(session);
        if (inventoryService is null)
        {
            return (false, false);
        }

        IReadOnlyList<ModelInventoryEntry> inventory =
            await inventoryService.GetAllAsync(cancellationToken).ConfigureAwait(false);
        ModelInventoryEntry? entry =
            LipSynthesisInventoryGate.ResolveEntry(inventory, preferredModelAlias);

        return (
            LipSynthesisInventoryGate.IsLicenseApproved(entry),
            LipSynthesisInventoryGate.AllowExperimentalExecution(entry));
    }

    /// <summary>
    /// Determines the overall run status from individual stage outcomes.
    /// </summary>
    internal static DubbingRunStatus DetermineOverallStatus(List<StageOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return DubbingRunStatus.Succeeded;
        }

        // A failed prerequisite stage blocks every subsequent stage in the runner, so no
        // deliverable can exist. Report Failed even when earlier stages succeeded:
        // PartialSuccess would let callers count a deliverable-free run as success (#327).
        bool prerequisiteFailed = outcomes.Any(o =>
            o.Status == StageStatus.Failed && DubbingPipelineStages.PrerequisiteStages.Contains(o.StageName));
        if (prerequisiteFailed)
        {
            return DubbingRunStatus.Failed;
        }

        bool anyFailed = outcomes.Any(o => o.Status == StageStatus.Failed);
        bool anySucceededOrPartial = outcomes.Any(o =>
            o.Status is StageStatus.Succeeded or StageStatus.PartiallySucceeded);
        bool allFullySucceeded = outcomes.All(o => o.Status == StageStatus.Succeeded);

        if (allFullySucceeded)
        {
            return DubbingRunStatus.Succeeded;
        }

        if (anyFailed && anySucceededOrPartial)
        {
            return DubbingRunStatus.PartialSuccess;
        }

        if (anyFailed)
        {
            return DubbingRunStatus.Failed;
        }

        bool anyNonBenignSkip = outcomes.Any(static o =>
            o.Status == StageStatus.Skipped
            && !IsBenignSkipReasonCode(o.ReasonCode));

        if (anyNonBenignSkip)
        {
            return anySucceededOrPartial ? DubbingRunStatus.PartialSuccess : DubbingRunStatus.Failed;
        }

        // A stage that completed with degraded output makes the run a partial success:
        // the artifacts exist, but the result is not what a clean run would produce.
        bool anyPartial = outcomes.Any(o => o.Status == StageStatus.PartiallySucceeded);
        if (anyPartial)
        {
            return DubbingRunStatus.PartialSuccess;
        }

        return DubbingRunStatus.Succeeded;
    }

    /// <summary>
    /// Reports a progress event to the optional progress reporter.
    /// </summary>
    private static void ReportProgress(
        IProgress<PipelineProgressEvent>? progress,
        string stageName,
        PipelineProgressEventKind eventKind,
        string? message,
        TimeSpan elapsed = default)
    {
        progress?.Report(new PipelineProgressEvent(
            StageName: stageName,
            EventKind: eventKind,
            Percentage: eventKind == PipelineProgressEventKind.Completed ? 100 : 0,
            Message: message,
            ElapsedDuration: elapsed));
    }

    /// <summary>
    /// Builds a <see cref="StageOutcome"/> for a skipped stage.
    /// </summary>
    private static StageOutcome BuildSkippedOutcome(string stageName, string reasonCode)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new StageOutcome
        {
            StageName = stageName,
            Status = StageStatus.Skipped,
            StartTime = now,
            EndTime = now,
            ArtifactPaths = [],
            ReasonCode = reasonCode,
        };
    }

    /// <summary>
    /// Builds a terminal <see cref="DubbingRunResult"/> for validation or pre-flight failures.
    /// </summary>
    private static DubbingRunResult BuildErrorResult(
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        DubbingRunStatus status,
        IReadOnlyList<string>? preFlightFailures = null,
        IReadOnlyDictionary<string, string>? executionSnapshot = null)
    {
        return new DubbingRunResult
        {
            RunId = runId,
            CorrelationId = runId,
            StartTime = runStart,
            EndTime = DateTimeOffset.UtcNow,
            OverallStatus = status,
            StageOutcomes = stageOutcomes.AsReadOnly(),
            PreFlightFailures = preFlightFailures,
            ExecutionSnapshot = executionSnapshot,
        };
    }

    /// <summary>
    /// Best-effort transient-fault publisher used by <see cref="ExecuteStageAsync"/>'s
    /// transient-exception arm. Wrapped in try/catch so telemetry can never break
    /// pipeline flow. Mirrors <see cref="StageRunHelper.PublishTransient"/>'s contract
    /// but skips stage-run persistence since the engine layer does not own that
    /// lifecycle. See <c>docs/internal/pipeline-readiness-spec.md</c> section 4.4.
    /// </summary>
    private void PublishEngineTransient(
        Guid projectId,
        string stageName,
        TransientFailureKind kind,
        Exception ex,
        DateTimeOffset stageStart,
        int attemptNumber = 1,
        Guid runId = default)
    {
        try
        {
            var context = new Dictionary<string, string>
            {
                ["Engine"] = "DubbingPipelineEngine",
                ["StageStart"] = stageStart.ToString("O"),
            };
            if (runId != Guid.Empty)
            {
                context["RunId"] = runId.ToString("N");
            }
            if (ex.GetType().FullName is { Length: > 0 } typeName)
            {
                context["ExceptionType"] = typeName;
            }
            _transientFaultBus.Publish(new PipelineTransientFault(
                projectId, stageName, kind, ex.Message, DateTimeOffset.UtcNow, attemptNumber, context));
        }
        catch
        {
            // Telemetry must never break pipeline flow — bus surface is best-effort.
        }
    }

    /// <summary>
    /// Streams transient-fault records emitted during this engine's lifetime.
    /// Bridges <see cref="PipelineTransientFaultBus"/> (IObservable) into an
    /// <see cref="IAsyncEnumerable{T}"/> via a <see cref="Channel{T}"/> so subscribers
    /// do not have to take an Rx dependency. Drains snapshot (already replayed by bus
    /// Subscribe), then forwards live events until the consumer cancels. See
    /// <c>docs/internal/pipeline-readiness-spec.md</c> section 4.4.
    /// </summary>
    public async IAsyncEnumerable<PipelineTransientFault> TransientFaultsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Bounded 256-capacity ring with DropOldest. Reasoning: a stalled consumer (e.g.
        // UI shell paused on UI thread) must not OOM the process. Stale faults are less
        // valuable than recent telemetry for the diagnostics-bundle snapshot cap; the bus
        // already retains its own 50-item ring so late-visible replay is still possible.
        var channel = Channel.CreateBounded<PipelineTransientFault>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        var observer = new ChannelWriterObserver(channel.Writer);
        using IDisposable subscription = _transientFaultBus.Subscribe(observer);
        try
        {
            await foreach (PipelineTransientFault fault in
                channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return fault;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed class ChannelWriterObserver(ChannelWriter<PipelineTransientFault> writer) : IObserver<PipelineTransientFault>
    {
        public void OnNext(PipelineTransientFault value) => writer.TryWrite(value);

        public void OnError(Exception error) => writer.TryComplete(error);

        public void OnCompleted() => writer.TryComplete();
    }
}
