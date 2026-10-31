using Trackdub.Application.Pipeline;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Translation;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Trackdub.Application.Transcripts;

public sealed partial class TranslationOrchestrationService(
    ITranslationRepository translationRepository,
    GlossaryService glossaryService,
    IGlossaryTermMatcher glossaryTermMatcher,
    ITranslationLanguageRouter translationLanguageRouter,
    ITranslationEngine translationEngine,
    ITtsTakeRepository ttsTakeRepository,
    IProjectStageRunStore stageRunStore,
    IArtifactStore artifactStore,
    TranscriptArtifactWriter artifactWriter,
    PipelineDegradationWriter? degradationWriter = null,
    ITranslatedWordAlignmentService? translatedWordAlignmentService = null,
    ILogger<TranslationOrchestrationService>? logger = null,
    IApplicationLogger? applicationLogger = null,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IAtomicRevisionCommitBoundary? commitBoundary = null,
    ITranslationModelProvisioner? modelProvisioner = null)
{
    // Sentinel language code meaning "follow the transcript's persisted or detected language".
    private const string AutoLanguageCode = "auto";

    // Serializes manifest read-modify-write operations to prevent concurrent language-change calls
    // from interleaving and clobbering each other's writes.
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    private readonly ITranslationRepository translationRepository = translationRepository ?? throw new ArgumentNullException(nameof(translationRepository));
    private readonly GlossaryService glossaryService = glossaryService ?? throw new ArgumentNullException(nameof(glossaryService));
    private readonly IGlossaryTermMatcher glossaryTermMatcher = glossaryTermMatcher ?? throw new ArgumentNullException(nameof(glossaryTermMatcher));
    private readonly ITranslationLanguageRouter translationLanguageRouter = translationLanguageRouter ?? throw new ArgumentNullException(nameof(translationLanguageRouter));
    private readonly ITranslationEngine translationEngine = translationEngine ?? throw new ArgumentNullException(nameof(translationEngine));
    private readonly ITranslatedWordAlignmentService translatedWordAlignmentService = translatedWordAlignmentService ?? new UnavailableTranslatedWordAlignmentService();
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly TranscriptArtifactWriter artifactWriter = artifactWriter ?? throw new ArgumentNullException(nameof(artifactWriter));
    private readonly ILogger<TranslationOrchestrationService> logger = logger ?? NullLogger<TranslationOrchestrationService>.Instance;
    private readonly IApplicationLogger? applicationLogger = applicationLogger;

    public async Task SetTranscriptLanguageAsync(
        TranscriptProjectState currentState,
        SetTranscriptLanguageRequest request,
        CancellationToken cancellationToken)
    {
        string? transcriptLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(request.TranscriptLanguage);
        if (string.Equals(currentState.TranscriptLanguage, transcriptLanguage, StringComparison.Ordinal))
        {
            return;
        }

        // Serialize the manifest read-modify-write so concurrent language-change calls cannot
        // interleave their reads and writes and lose each other's updates.
        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectManifest manifest = await ReadProjectManifestAsync(
                currentState.ProjectState.Project,
                currentState.TranscriptLanguage,
                cancellationToken).ConfigureAwait(false);
            string? selectedTranslationTargetLanguage =
                TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCodeOrNull(
                    request.SelectedTranslationTargetLanguage);
            ProjectUiSettings uiSettings = (manifest.UiSettings ?? new ProjectUiSettings()).Normalize() with
            {
                SelectedTranslationTargetLanguage = selectedTranslationTargetLanguage
                    ?? manifest.UiSettings?.SelectedTranslationTargetLanguage,
            };

            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.ManifestRelativePath,
                manifest
                    .WithTranscriptLanguage(transcriptLanguage)
                    .WithUiSettings(uiSettings),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    /// Resolves the effective source language for a bulk translation run.
    /// A null, empty, or "auto" request follows project truth: the transcript language persisted
    /// at ASR time, falling back to the dominant per-segment detected language (persisting it so
    /// later stages and reloads see the same value). An explicit code must match the persisted
    /// transcript language; when none is persisted yet, the explicit code is adopted and persisted.
    /// </summary>
    private async Task<string> ResolveSourceLanguageAsync(
        TranscriptProjectState currentState,
        string? requestedSourceLanguage,
        CancellationToken cancellationToken)
    {
        string? requested = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(requestedSourceLanguage);
        string? transcriptLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(currentState.TranscriptLanguage);
        bool followProjectLanguage = requested is null || string.Equals(requested, AutoLanguageCode, StringComparison.Ordinal);

        if (!followProjectLanguage)
        {
            if (transcriptLanguage is null)
            {
                await SetTranscriptLanguageAsync(
                    currentState,
                    new SetTranscriptLanguageRequest(requested),
                    cancellationToken).ConfigureAwait(false);
                return requested!;
            }

            if (!string.Equals(transcriptLanguage, requested, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Requested source language '{requested}' does not match the transcript language '{transcriptLanguage}'. " +
                    "Change the transcript language first or request translation with the matching source language.");
            }

            return requested!;
        }

        if (transcriptLanguage is not null && !string.Equals(transcriptLanguage, AutoLanguageCode, StringComparison.Ordinal))
        {
            return transcriptLanguage;
        }

        string? detectedLanguage = TranscriptWorkflowUtilities.ResolveDetectedTranscriptLanguage(currentState.TranscriptSegments);
        if (detectedLanguage is not null && !string.Equals(detectedLanguage, AutoLanguageCode, StringComparison.Ordinal))
        {
            await SetTranscriptLanguageAsync(
                currentState,
                new SetTranscriptLanguageRequest(detectedLanguage),
                cancellationToken).ConfigureAwait(false);
            return detectedLanguage;
        }

        throw new InvalidOperationException(
            "Source language is unknown: no transcript language is set and ASR did not detect one. " +
            "Set the transcript language or request translation with an explicit source language.");
    }

    public async Task SetSelectedTranslationTargetLanguageAsync(
        TranscriptProjectState currentState,
        string? targetLanguage,
        CancellationToken cancellationToken)
    {
        string? normalizedTargetLanguage =
            TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCodeOrNull(targetLanguage);
        ProjectUiSettings currentUiSettings = currentState.ProjectState.UiSettings?.Normalize() ?? new ProjectUiSettings();
        if (string.Equals(
                currentUiSettings.SelectedTranslationTargetLanguage,
                normalizedTargetLanguage,
                StringComparison.Ordinal))
        {
            return;
        }

        await _manifestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProjectManifest manifest = await ReadProjectManifestAsync(
                currentState.ProjectState.Project,
                currentState.TranscriptLanguage,
                cancellationToken).ConfigureAwait(false);
            ProjectUiSettings uiSettings = (manifest.UiSettings ?? currentUiSettings).Normalize() with
            {
                SelectedTranslationTargetLanguage = normalizedTargetLanguage,
            };

            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.ManifestRelativePath,
                manifest.WithUiSettings(uiSettings),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    public Task GenerateTranslationAsync(
        TranscriptProjectState currentState,
        GenerateTranslationRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null) =>
        GenerateTranslationAsync(currentState, request, cancellationToken, progress, segmentObserver: null);

    internal async Task GenerateTranslationAsync(
        TranscriptProjectState currentState,
        GenerateTranslationRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress,
        ITranslatedSegmentObserver? segmentObserver)
    {
        DateTimeOffset progressStartedAt = DateTimeOffset.UtcNow;
        PipelineProgressReporter.Started(progress, StageNames.Translation, phase: "Preparing");

        TranscriptRevision currentTranscriptRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranslationRunPreparation? preparation =
            await PrepareTranslationRunAsync(currentState, request, cancellationToken).ConfigureAwait(false);
        if (preparation is null)
        {
            // Record a benign skipped run so the pipeline engine can bind this invocation to
            // a stage row instead of reporting STAGE_RUN_MISSING and blocking downstream stages.
            StageRunRecord skippedTranslationRun = await StageRunHelper
                .StartAsync(stageRunStore, currentState.ProjectState.Project.Id, StageNames.Translation, cancellationToken)
                .ConfigureAwait(false);
            await StageRunHelper
                .SkipAsync(stageRunStore, skippedTranslationRun, null, StageSkipReasonCodes.ExistingArtifactsValid, cancellationToken)
                .ConfigureAwait(false);
            PipelineProgressReporter.Skipped(progress, StageNames.Translation,
                "No translations need updating.", DateTimeOffset.UtcNow - progressStartedAt);
            return;
        }
        (string sourceLanguage, string targetLanguage, TranslationScopePlan scopePlan) = preparation;

        PipelineProgressReporter.Phase(progress, StageNames.Translation, "Resolving model");
        string? provisionedAlias = modelProvisioner is null ? request.PreferredModelAlias :
            await modelProvisioner.EnsureAsync(sourceLanguage, targetLanguage, request.PreferredModelAlias, cancellationToken,
                new TranslationPreparationProgress(progress)).ConfigureAwait(false);
        PipelineProgressReporter.Phase(progress, StageNames.Translation, "Initializing translation model");
        TranslationRouteSelection route = await translationLanguageRouter.ResolveRouteAsync(
            sourceLanguage,
            targetLanguage,
            cancellationToken,
            provisionedAlias).ConfigureAwait(false);
        if (!route.IsAvailable)
        {
            throw new InvalidOperationException(
                route.UnavailableReason ??
                $"Translation route {sourceLanguage} -> {targetLanguage} is not available.");
        }

        StageRunRecord translationStageRun = await StageRunHelper
            .StartAsync(stageRunStore, currentState.ProjectState.Project.Id, StageNames.Translation, cancellationToken)
            .ConfigureAwait(false);

        TranslationStreamCommit? streamCommit = null;
        IReadOnlyList<TranslatedTextSegment> translatedTextSegments;
        try
        {
            PipelineProgressReporter.Phase(progress, StageNames.Translation, "Preparing segments");
            IReadOnlyList<GlossaryEntry> glossaryEntries = await glossaryService.GetMergedEntriesAsync(
                currentState.ProjectState.Project.Id,
                sourceLanguage,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);
            TranslationInputSegment[] translationInputSegments = scopePlan.SelectedSegments
                .OrderBy(segment => segment.SegmentIndex)
                .Select(segment => new TranslationInputSegment(
                    segment.SegmentIndex,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    segment.Text))
                .ToArray();
            IReadOnlyList<TranslationGlossaryHint> glossaryHints = glossaryTermMatcher.BuildHints(
                sourceLanguage,
                translationInputSegments,
                glossaryEntries);

            PipelineProgressReporter.Phase(
                progress,
                StageNames.Translation,
                "Translating",
                $"{translationInputSegments.Length} segment(s) queued.");
            var translationRequest = new TranslationRequest(
                sourceLanguage,
                targetLanguage,
                translationInputSegments,
                PreferredModelAlias: request.PreferredModelAlias,
                GlossaryHints: glossaryHints,
                PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias);
            if (request.EnableSegmentStreaming && translationEngine is IStreamingTranslationEngine streamingEngine)
            {
                streamCommit = await CollectTranslationStreamAsync(
                    streamingEngine,
                    translationRequest,
                    translationStageRun.Id,
                    currentTranscriptRevision.Id,
                    targetLanguage,
                    translationInputSegments.Length,
                    progress,
                    segmentObserver,
                    cancellationToken).ConfigureAwait(false);
                translatedTextSegments = streamCommit.Items.Select(item => item.Payload).ToArray();
            }
            else
            {
                translatedTextSegments = await translationEngine.TranslateAsync(
                    translationRequest,
                    cancellationToken).ConfigureAwait(false);
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            TranslationExecutionMetadata? executionMetadata = GetTranslationExecutionMetadata(translationEngine);
            TranslationRevision translationRevision = TranslationRevision.Create(
                currentState.ProjectState.Project.Id,
                translationStageRun.Id,
                currentTranscriptRevision.Id,
                targetLanguage,
                revisionNumber: 1,
                now,
                translationProvider: executionMetadata?.ProviderName ?? route.ProviderName,
                modelId: executionMetadata?.ModelId ?? route.ModelId,
                executionProvider: executionMetadata?.SelectedExecutionProvider,
                modelProvenance: executionMetadata?.ModelProvenance ?? route.ModelProvenance);
            Dictionary<int, TranscriptSegment> sourceSegmentsByIndex = currentState.TranscriptSegments
                .OrderBy(segment => segment.SegmentIndex)
                .ToDictionary(segment => segment.SegmentIndex);

            List<TranslatedSegment> translatedSegments = [];
            TranslatedTextSegment[] orderedTranslatedTextSegments = translatedTextSegments
                .OrderBy(static segment => segment.Index)
                .ToArray();
            int processedTranslatedTextSegments = 0;
            PipelineProgressReporter.Phase(
                progress,
                StageNames.Translation,
                "Building revision",
                $"{orderedTranslatedTextSegments.Length} segment(s) returned.");
            foreach (TranslatedTextSegment segment in orderedTranslatedTextSegments)
            {
                bool hasText = !string.IsNullOrEmpty(segment.Text);
                bool hasSourceSegment = sourceSegmentsByIndex.TryGetValue(segment.Index, out TranscriptSegment? sourceSegment);
                string? text = hasText
                    ? segment.Text
                    : (hasSourceSegment
                        ? sourceSegment!.Text
                        : null);

                // If neither the engine output nor the source fallback produced usable text,
                // skip this segment rather than passing empty text to TranslatedSegment.Create,
                // which rejects null/empty/whitespace text with ArgumentException.
                if (string.IsNullOrWhiteSpace(text))
                {
                    processedTranslatedTextSegments++;
                    PipelineProgressReporter.Determinate(
                        progress,
                        StageNames.Translation,
                        processedTranslatedTextSegments,
                        orderedTranslatedTextSegments.Length,
                        "Building revision",
                        currentItemLabel: $"Segment {segment.Index}");
                    continue;
                }

                TranslatedSegment translatedSegment = TranslatedSegment.Create(
                    translationRevision.Id,
                    segment.Index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    text,
                    hasText && hasSourceSegment
                        ? TranscriptWorkflowUtilities.ComputeSourceSegmentHash(sourceSegment!)
                        : null) with
                { Origin = TranslationSegmentOrigin.Generated };

                if (hasText && hasSourceSegment)
                {
                    translatedSegment = await AlignTranslatedWordsAsync(
                        translatedSegment,
                        sourceSegment!,
                        sourceLanguage,
                        targetLanguage,
                        request.PreferredModelAlias,
                        request.PreferredExecutionProvider,
                        request.RequirePreferredExecutionProvider,
                        cancellationToken).ConfigureAwait(false);
                }

                translatedSegments.Add(translatedSegment);
                processedTranslatedTextSegments++;
                PipelineProgressReporter.Determinate(
                    progress,
                    StageNames.Translation,
                    processedTranslatedTextSegments,
                    orderedTranslatedTextSegments.Length,
                    "Building revision",
                    currentItemLabel: $"Segment {segment.Index}");
            }

            var revisionSegments = scopePlan.BuildRevision(translationRevision.Id, translatedTextSegments, translatedSegments);

            PipelineProgressReporter.Phase(progress, StageNames.Translation, "Saving translation");
            IAtomicRevisionCommitBoundary boundary = commitBoundary
                ?? throw new InvalidOperationException("Atomic revision commit boundary is not configured.");
            translationRevision = await boundary.CommitRevisionAsync(
                new AtomicRevisionCommitRequest(translationRevision, scopePlan.PreviousRevision?.Id, revisionSegments, streamCommit),
                (revision, ct) => artifactWriter.PrepareTranslationArtifactAsync(
                    currentState.ProjectState.Project.Id, TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
                    revision, revisionSegments, translationStageRun.Id, "generated-translation", ct),
                cancellationToken).ConfigureAwait(false);

            if (degradationWriter is not null)
            {
                int[] emptyIndices = translatedTextSegments
                    .Where(static s => string.IsNullOrEmpty(s.Text))
                    .Select(static s => s.Index)
                    .ToArray();

                if (emptyIndices.Length > 0)
                {
                    // Aggregate all empty-output segments into a single degradation record so
                    // long transcripts with widespread engine failures don't flood the artifact
                    // store with one record per segment.
                    const int maxListed = 10;
                    string indexSummary = emptyIndices.Length <= maxListed
                        ? string.Join(", ", emptyIndices)
                        : string.Join(", ", emptyIndices.Take(maxListed)) + $" … ({emptyIndices.Length - maxListed} more)";

                    try
                    {
                        await degradationWriter.WriteAsync(
                            new PipelineDegradationRecord(
                                StageNames.Translation,
                                "TRANSLATION_EMPTY_OUTPUT",
                                $"Translation engine returned empty output for {emptyIndices.Length} segment(s) (indices: {indexSummary}); source text used as fallback.",
                                Detail: null,
                                SelectedFallback: "source-text",
                                RecommendedAction: "Review source text for the affected segments or try a different translation model.",
                                DateTimeOffset.UtcNow,
                                translationStageRun.Id),
                            currentState.ProjectState.Project.Id,
                            TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState).Id,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Degradation write is best-effort; failure must not abort the translation persistence path.
                    }
                }
            }

            if (degradationWriter is not null)
            {
                // Identify segments where both the engine output and the source-text fallback
                // were unusable, causing the segment to be dropped from the revision entirely.
                // These are distinct from TRANSLATION_EMPTY_OUTPUT (engine empty but source
                // fallback succeeded): here no text at all could be produced, so downstream
                // stages (TTS, alignment) will see a gap in segment indices.
                HashSet<int> builtIndices = translatedSegments
                    .Select(static s => s.SegmentIndex)
                    .ToHashSet();
                int[] droppedIndices = translatedTextSegments
                    .Select(static s => s.Index)
                    .Where(i => !builtIndices.Contains(i))
                    .ToArray();

                if (droppedIndices.Length > 0)
                {
                    const int maxListed = 10;
                    string indexSummary = droppedIndices.Length <= maxListed
                        ? string.Join(", ", droppedIndices)
                        : string.Join(", ", droppedIndices.Take(maxListed)) + $" … ({droppedIndices.Length - maxListed} more)";

                    try
                    {
                        await degradationWriter.WriteAsync(
                            new PipelineDegradationRecord(
                                StageNames.Translation,
                                "TRANSLATION_SEGMENT_DROPPED",
                                $"Translation produced no usable text for {droppedIndices.Length} segment(s) (indices: {indexSummary}); " +
                                "engine output was empty and no source-text fallback was available. These segments are absent from the revision.",
                                Detail: null,
                                SelectedFallback: null,
                                RecommendedAction: "Check that all transcript segment indices are present in the translation engine output and that the source transcript is not empty for these indices.",
                                DateTimeOffset.UtcNow,
                                translationStageRun.Id),
                            currentState.ProjectState.Project.Id,
                            TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState).Id,
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Best-effort: degradation write failure must not abort translation persistence.
                    }
                }
            }

            int[] allSegmentIndices = currentState.TranscriptSegments
                .Select(static segment => segment.SegmentIndex)
                .ToArray();
            ProjectUiSettings updatedUiSettings = SegmentStageRunProvenanceStore.RecordTranslationRuns(
                currentState.ProjectUiSettings,
                allSegmentIndices,
                scopePlan.SelectedSegments.Select(segment => segment.SegmentIndex).ToHashSet(),
                translationStageRun.Id,
                scopePlan.PreviousRevision?.StageRunId) with
            {
                SelectedTranslationTargetLanguage = targetLanguage,
            };
            await SegmentStageRunProvenanceStore.PersistUiSettingsAsync(
                artifactStore,
                currentState.ProjectState.Project,
                currentState.TranscriptLanguage,
                updatedUiSettings,
                cancellationToken).ConfigureAwait(false);

            // Mark the run completed only after the revision, stale-marking, artifact, and
            // provenance persist — a Completed row must imply durable output.
            translationStageRun = await StageRunHelper
                .CompleteAsync(stageRunStore, translationStageRun, translationEngine, cancellationToken, runtimePlanningPreferences)
                .ConfigureAwait(false);
        }
        // Only the caller's token means user cancellation. A TaskCanceledException from a
        // cloud-engine timeout must fall through to the failure arm, not read as "canceled".
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, translationStageRun, translationEngine, "Translation canceled.", CancellationToken.None, runtimePlanningPreferences)
                .ConfigureAwait(false);
            PipelineProgressReporter.Failed(
                progress,
                StageNames.Translation,
                "Translation canceled.",
                DateTimeOffset.UtcNow - progressStartedAt);
            throw;
        }
        catch (Exception ex)
        {
            await StageRunHelper
                .FailAsync(stageRunStore, translationStageRun, translationEngine, ex.Message, cancellationToken, runtimePlanningPreferences)
                .ConfigureAwait(false);
            PipelineProgressReporter.Failed(
                progress,
                StageNames.Translation,
                ex.Message,
                DateTimeOffset.UtcNow - progressStartedAt);
            throw;
        }

        PipelineProgressReporter.Completed(
            progress,
            StageNames.Translation,
            DateTimeOffset.UtcNow - progressStartedAt,
            "Translation finished.");
    }

    public async Task RetranslateSegmentAsync(
        TranscriptProjectState currentState,
        RetranslateSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentTranscriptRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranslationRevision currentTranslationRevision = currentState.CurrentTranslationRevision
            ?? throw new InvalidOperationException("Generate translation before re-translating a segment.");

        if (request.TranslationRevisionId != currentTranslationRevision.Id)
        {
            throw new InvalidOperationException("The selected translation revision is no longer current.");
        }

        string sourceLanguage = TranscriptWorkflowUtilities.NormalizeRequiredTranscriptLanguageCode(request.SourceLanguage);
        string targetLanguage = TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCode(request.TargetLanguage);
        if (!string.Equals(currentState.TranscriptLanguage, sourceLanguage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Set the transcript language before starting translation.");
        }

        TranscriptSegment sourceSegment = currentState.TranscriptSegments
            .FirstOrDefault(segment => segment.Id == request.SegmentId)
            ?? throw new InvalidOperationException("The selected transcript segment is no longer available.");

        string? provisionedAlias = modelProvisioner is null ? request.PreferredModelAlias :
            await modelProvisioner.EnsureAsync(sourceLanguage, targetLanguage, request.PreferredModelAlias, cancellationToken).ConfigureAwait(false);
        TranslationRouteSelection route = await translationLanguageRouter.ResolveRouteAsync(
            sourceLanguage,
            targetLanguage,
            cancellationToken,
            provisionedAlias).ConfigureAwait(false);
        if (!route.IsAvailable)
        {
            throw new InvalidOperationException(
                route.UnavailableReason ??
                $"Translation route {sourceLanguage} -> {targetLanguage} is not available.");
        }

        StageRunRecord translationStageRun = await StageRunHelper
            .StartAsync(stageRunStore, currentState.ProjectState.Project.Id, StageNames.Translation, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<TranslatedTextSegment> translatedTextSegments;
        try
        {
            translatedTextSegments = await translationEngine.TranslateAsync(
                new TranslationRequest(
                    sourceLanguage,
                    targetLanguage,
                    [
                        new TranslationInputSegment(
                            sourceSegment.SegmentIndex,
                            sourceSegment.StartSeconds,
                            sourceSegment.EndSeconds,
                            sourceSegment.Text)
                    ],
                    PreferredModelAlias: request.PreferredModelAlias,
                    PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
                    RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                    PreferredModelVariantAlias: request.PreferredModelVariantAlias),
                cancellationToken).ConfigureAwait(false);

            TranslatedTextSegment? translatedTextSegment = translatedTextSegments
                .FirstOrDefault(segment => segment.Index == sourceSegment.SegmentIndex);
            bool hasUsableEngineText = !string.IsNullOrWhiteSpace(translatedTextSegment?.Text);
            string replacementText = hasUsableEngineText
                ? translatedTextSegment!.Text
                : sourceSegment.Text;

            int nextRevisionNumber = await translationRepository.GetNextRevisionNumberAsync(
                currentState.ProjectState.Project.Id,
                targetLanguage,
                cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            TranslationExecutionMetadata? executionMetadata = GetTranslationExecutionMetadata(translationEngine);
            TranslationRevision translationRevision = TranslationRevision.Create(
                currentState.ProjectState.Project.Id,
                translationStageRun.Id,
                currentTranscriptRevision.Id,
                targetLanguage,
                nextRevisionNumber,
                now,
                translationProvider: executionMetadata?.ProviderName ?? route.ProviderName,
                modelId: executionMetadata?.ModelId ?? route.ModelId,
                executionProvider: executionMetadata?.SelectedExecutionProvider,
                modelProvenance: executionMetadata?.ModelProvenance ?? route.ModelProvenance);

            TranslatedSegment replacementSegment = TranslatedSegment.Create(
                translationRevision.Id,
                sourceSegment.SegmentIndex,
                sourceSegment.StartSeconds,
                sourceSegment.EndSeconds,
                replacementText,
                hasUsableEngineText
                    ? TranscriptWorkflowUtilities.ComputeSourceSegmentHash(sourceSegment)
                    : null) with
            { Origin = TranslationSegmentOrigin.Generated };
            if (hasUsableEngineText)
            {
                replacementSegment = await AlignTranslatedWordsAsync(
                    replacementSegment,
                    sourceSegment,
                    sourceLanguage,
                    targetLanguage,
                    request.PreferredModelAlias,
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider,
                    cancellationToken).ConfigureAwait(false);
            }
            TranslatedSegment? existingTargetSegment = currentState.TranslatedSegments
                .FirstOrDefault(segment => segment.SegmentIndex == sourceSegment.SegmentIndex);
            bool changed = existingTargetSegment is null ||
                           !string.Equals(existingTargetSegment.Text, replacementSegment.Text, StringComparison.Ordinal);

            // Use PatchRevisionAsync: only the single changed/new segment is passed; the repository
            // handles re-binding unchanged segments from the previous revision.  This avoids the O(n)
            // in-memory reconstruction for the common single-segment retranslation case.
            await translationRepository.PatchRevisionAsync(
                currentTranslationRevision, translationRevision, [replacementSegment], cancellationToken)
                .ConfigureAwait(false);

            // Read back the merged list so the artifact writer has the full set.
            IReadOnlyList<TranslatedSegment> translatedSegments = await translationRepository
                .GetSegmentsAsync(translationRevision.Id, cancellationToken)
                .ConfigureAwait(false);

            if (changed)
            {
                await ttsTakeRepository.MarkBySegmentIndicesStaleAsync(
                    currentState.ProjectState.Project.Id,
                    new HashSet<int> { sourceSegment.SegmentIndex },
                    cancellationToken).ConfigureAwait(false);
            }

            await artifactWriter.WriteTranslationArtifactAsync(
                currentState.ProjectState.Project.Id,
                TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
                translationRevision,
                translatedSegments,
                translationStageRun.Id,
                provenance: "single-segment-retranslation",
                cancellationToken).ConfigureAwait(false);

            int[] allSegmentIndices = currentState.TranscriptSegments
                .Select(static segment => segment.SegmentIndex)
                .ToArray();
            ProjectUiSettings updatedUiSettings = SegmentStageRunProvenanceStore.RecordTranslationRuns(
                currentState.ProjectUiSettings,
                allSegmentIndices,
                new HashSet<int> { sourceSegment.SegmentIndex },
                translationStageRun.Id,
                currentTranslationRevision.StageRunId);
            await SegmentStageRunProvenanceStore.PersistUiSettingsAsync(
                artifactStore,
                currentState.ProjectState.Project,
                currentState.TranscriptLanguage,
                updatedUiSettings,
                cancellationToken).ConfigureAwait(false);

            // Complete the run only after the revision patch, stale-marking, artifact, and
            // provenance persist — a Completed row must imply durable output.
            translationStageRun = await StageRunHelper
                .CompleteAsync(stageRunStore, translationStageRun, translationEngine, cancellationToken, runtimePlanningPreferences)
                .ConfigureAwait(false);
            applicationLogger?.LogInformation(
                PipelineRuntimeProvenanceFormatter.FormatStageSegmentLogLine(
                    StageNames.Translation,
                    sourceSegment.SegmentIndex,
                    translationStageRun.RuntimeInfo));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, translationStageRun, translationEngine, "Translation canceled.", CancellationToken.None, runtimePlanningPreferences)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await StageRunHelper
                .FailAsync(stageRunStore, translationStageRun, translationEngine, ex.Message, cancellationToken, runtimePlanningPreferences)
                .ConfigureAwait(false);
            throw;
        }
    }

    public async Task SaveTranslationEditsAsync(
        TranscriptProjectState currentState,
        SaveTranslationEditsRequest request,
        CancellationToken cancellationToken)
    {
        TranslationRevision currentTranslationRevision = currentState.CurrentTranslationRevision
            ?? throw new InvalidOperationException("The project does not contain a translation revision.");

        if (currentTranslationRevision.Id != request.TranslationRevisionId)
        {
            throw new InvalidOperationException("Translation edits were based on an out-of-date revision.");
        }

        string targetLanguage = TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCode(
            request.TargetLanguage);
        if (!string.Equals(currentTranslationRevision.TargetLanguage, targetLanguage, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Translation edits were based on a different target language.");
        }

        // Trim before comparing so whitespace-only edits are treated as unchanged and do not
        // drop word alignment or mark TTS takes stale. Empty results are ignored entirely.
        Dictionary<int, string> replacements = request.Segments
            .Select(segment => (segment.SegmentIndex, Text: segment.Text.Trim()))
            .Where(entry => entry.Text.Length > 0)
            .ToDictionary(entry => entry.SegmentIndex, entry => entry.Text);
        int nextRevisionNumber = await translationRepository.GetNextRevisionNumberAsync(
            currentState.ProjectState.Project.Id,
            targetLanguage,
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        TranslationRevision editedRevision = TranslationRevision.Create(
            currentState.ProjectState.Project.Id,
            stageRunId: null,
            currentTranslationRevision.SourceTranscriptRevisionId,
            targetLanguage,
            nextRevisionNumber,
            now,
            currentTranslationRevision.TranslationProvider,
            currentTranslationRevision.ModelId,
            currentTranslationRevision.ExecutionProvider,
            modelProvenance: currentTranslationRevision.ModelProvenance);
        HashSet<int> changedSegmentIndices = currentState.TranslatedSegments
            .Where(segment => replacements.TryGetValue(segment.SegmentIndex, out string? replacement) &&
                              !string.Equals(segment.Text, replacement, StringComparison.Ordinal))
            .Select(segment => segment.SegmentIndex)
            .ToHashSet();

        // Build only the changed segments; PatchRevisionAsync handles re-binding unchanged segments
        // from the previous revision, avoiding the O(n) full reconstruction when only a subset changed.
        TranslatedSegment[] changedSegmentObjects = currentState.TranslatedSegments
            .Where(segment => replacements.ContainsKey(segment.SegmentIndex))
            .Select(segment => CreateEditedTranslationSegment(
                editedRevision,
                segment,
                replacements[segment.SegmentIndex],
                changedSegmentIndices))
            .ToArray();

        await translationRepository.PatchRevisionAsync(
            currentTranslationRevision, editedRevision, changedSegmentObjects, cancellationToken)
            .ConfigureAwait(false);

        // Read back the merged list so the artifact writer has the full set.
        IReadOnlyList<TranslatedSegment> editedSegments = await translationRepository
            .GetSegmentsAsync(editedRevision.Id, cancellationToken)
            .ConfigureAwait(false);

        if (changedSegmentIndices.Count > 0)
        {
            await ttsTakeRepository.MarkBySegmentIndicesStaleAsync(
                currentState.ProjectState.Project.Id,
                changedSegmentIndices,
                cancellationToken).ConfigureAwait(false);
        }

        await artifactWriter.WriteTranslationArtifactAsync(
            currentState.ProjectState.Project.Id,
            TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
            editedRevision,
            editedSegments,
            stageRunId: null,
            provenance: "manual-edit",
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RestoreTranslationRevisionAsync(
        TranscriptProjectState currentState,
        string targetLanguage,
        IReadOnlyList<TranslatedSegment> translatedSegments,
        CancellationToken cancellationToken)
    {
        TranslationRevision? sourceRevision = currentState.CurrentTranslationRevision;
        int nextRevisionNumber = await translationRepository.GetNextRevisionNumberAsync(
            currentState.ProjectState.Project.Id,
            targetLanguage,
            cancellationToken).ConfigureAwait(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TranslationRevision restoredRevision = TranslationRevision.Create(
            currentState.ProjectState.Project.Id,
            stageRunId: null,
            currentState.CurrentTranscriptRevision!.Id,
            targetLanguage,
            nextRevisionNumber,
            now,
            sourceRevision?.TranslationProvider,
            sourceRevision?.ModelId,
            sourceRevision?.ExecutionProvider,
            modelProvenance: sourceRevision?.ModelProvenance);

        TranslatedSegment[] restoredSegments = translatedSegments
            .OrderBy(segment => segment.SegmentIndex)
            .Select(segment => TranslatedSegment.Create(
                restoredRevision.Id,
                segment.SegmentIndex,
                segment.StartSeconds,
                segment.EndSeconds,
                segment.Text,
                segment.SourceSegmentHash,
                segment.Words) with
            { Origin = segment.Origin })
            .ToArray();

        await translationRepository.SaveRevisionAsync(restoredRevision, restoredSegments, cancellationToken).ConfigureAwait(false);
        await artifactWriter.WriteTranslationArtifactAsync(
            currentState.ProjectState.Project.Id,
            TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
            restoredRevision,
            restoredSegments,
            stageRunId: null,
            provenance: "history-restore",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProjectManifest> ReadProjectManifestAsync(
        TrackdubProject project,
        string? transcriptLanguage,
        CancellationToken cancellationToken)
    {
        ProjectManifest? manifest = await artifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            cancellationToken).ConfigureAwait(false);
        return manifest ?? ProjectManifest.FromProject(project, transcriptLanguage);
    }

    /// <summary>
    /// Opt-in (<see cref="GenerateTranslationRequest.EnableSegmentStreaming"/>): drives a
    /// streaming translation engine through the bounded channel, retaining every item's
    /// envelope for final identity validation at the commit boundary. Persistence
    /// happens once after the complete stream; nothing here commits a partial revision.
    /// </summary>
    private static async Task<TranslationStreamCommit> CollectTranslationStreamAsync(
        IStreamingTranslationEngine streamingEngine, TranslationRequest translationRequest, Guid runId,
        Guid sourceRevisionId, string targetLanguage, int inputSegmentCount,
        IProgress<PipelineProgressEvent>? progress, ITranslatedSegmentObserver? segmentObserver,
        CancellationToken cancellationToken)
    {
        string snapshotId = $"{sourceRevisionId:N}:{targetLanguage}";
        List<PipelineStreamItem<TranslatedTextSegment>> collected = [];
        await BoundedPipelineRunner.RunAsync<TranslatedTextSegment>(
            new BoundedPipelineChannelOptions(itemCapacity: 8, byteCapacity: 4 * 1024 * 1024),
            async (channel, ct) =>
            {
                await foreach (var item in streamingEngine.TranslateStreamAsync(translationRequest, runId, snapshotId, sourceRevisionId, ct).ConfigureAwait(false))
                    await channel.WriteAsync(item, ct).ConfigureAwait(false);
            },
            async (items, ct) =>
            {
                await foreach (var item in items.WithCancellation(ct).ConfigureAwait(false))
                {
                    // Retain the complete envelope. Revision acceptance belongs to the real commit boundary.
                    collected.Add(item);
                    segmentObserver?.OnSegmentTranslated(item.Payload.Index, item.Payload.Text);
                    PipelineProgressReporter.Phase(progress, StageNames.Translation, "Translating",
                        $"{collected.Count}/{inputSegmentCount} segment(s) translated.");
                }
            }, cancellationToken).ConfigureAwait(false);
        return new TranslationStreamCommit(runId, snapshotId, sourceRevisionId,
            translationRequest.Segments.Select(segment => segment.Index).ToArray(), collected);
    }

    private sealed class TranslationPreparationProgress(IProgress<PipelineProgressEvent>? progress) : IProgress<string>
    {
        public void Report(string value) => PipelineProgressReporter.Phase(progress, StageNames.Translation, value);
    }

    private static TranslationExecutionMetadata? GetTranslationExecutionMetadata(object stageEngine) =>
        stageEngine is ITranslationExecutionMetadataReporter reporter
            ? reporter.LastExecutionMetadata
            : null;

    private async Task<TranslatedSegment> AlignTranslatedWordsAsync(
        TranslatedSegment translatedSegment,
        TranscriptSegment sourceSegment,
        string sourceLanguage,
        string targetLanguage,
        string? preferredModelAlias,
        ExecutionProviderKind? preferredExecutionProvider,
        bool requirePreferredExecutionProvider,
        CancellationToken cancellationToken)
    {
        TranslatedWordAlignmentResult alignment;
        try
        {
            alignment = await translatedWordAlignmentService.AlignAsync(
                new TranslatedWordAlignmentRequest(
                    sourceSegment,
                    translatedSegment,
                    sourceLanguage,
                    targetLanguage,
                    preferredModelAlias,
                    preferredExecutionProvider,
                    requirePreferredExecutionProvider),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Word alignment failed for segment {SegmentIndex}; karaoke timing will be omitted for this segment.",
                translatedSegment.SegmentIndex);
            return translatedSegment with { Words = [] };
        }

        if (alignment.Outcome is not TranslatedWordAlignmentOutcomeKind.Succeeded)
        {
            return translatedSegment with { Words = [] };
        }

        return HasRenderableTranslatedWordAlignment(translatedSegment, alignment.Words)
            ? new TranslatedSegment(
                translatedSegment.Id,
                translatedSegment.TranslationRevisionId,
                translatedSegment.SegmentIndex,
                translatedSegment.StartSeconds,
                translatedSegment.EndSeconds,
                translatedSegment.Text,
                translatedSegment.SourceSegmentHash,
                alignment.Words) with
            { Origin = translatedSegment.Origin }
            : translatedSegment with { Words = [] };
    }

    private static bool HasRenderableTranslatedWordAlignment(
        TranslatedSegment translatedSegment,
        IReadOnlyList<TranslatedWord> words)
    {
        if (words.Count == 0)
        {
            return false;
        }

        double previousEnd = translatedSegment.StartSeconds;
        foreach (TranslatedWord word in words.OrderBy(static word => word.WordIndex))
        {
            if (word.StartSeconds < translatedSegment.StartSeconds ||
                word.EndSeconds > translatedSegment.EndSeconds ||
                word.StartSeconds < previousEnd)
            {
                return false;
            }

            previousEnd = word.EndSeconds;
        }

        return CanMapWordsToTranslatedText(translatedSegment.Text, words);
    }

    private static bool CanMapWordsToTranslatedText(
        string translatedText,
        IReadOnlyList<TranslatedWord> words)
    {
        int searchStart = 0;
        foreach (TranslatedWord word in words.OrderBy(static word => word.WordIndex))
        {
            int matchIndex = translatedText.IndexOf(word.Text, searchStart, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                return false;
            }

            searchStart = matchIndex + word.Text.Length;
        }

        return true;
    }
}
