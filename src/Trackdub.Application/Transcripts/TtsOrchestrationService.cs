using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trackdub.Application.Artifacts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class TtsOrchestrationService(
    StartTtsStageHandler startTtsStageHandler,
    IVoiceAssignmentRepository voiceAssignmentRepository,
    ITtsTakeRepository ttsTakeRepository,
    ITtsEngine ttsEngine,
    IVoiceCatalog voiceCatalog,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    IReferenceClipTrimmer referenceClipTrimmer,
    DurationAnalysisService? durationAnalysisService = null,
    IAudioTimeStretchService? audioTimeStretchService = null,
    TtsTimingOptions? timingOptions = null,
    IAudioClipExtractor? audioClipExtractor = null,
    IReferenceClipAnalyzer? referenceClipAnalyzer = null)
{
    private readonly StartTtsStageHandler startTtsStageHandler = startTtsStageHandler ?? throw new ArgumentNullException(nameof(startTtsStageHandler));
    private readonly IVoiceAssignmentRepository voiceAssignmentRepository = voiceAssignmentRepository ?? throw new ArgumentNullException(nameof(voiceAssignmentRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly ITtsEngine ttsEngine = ttsEngine ?? throw new ArgumentNullException(nameof(ttsEngine));
    private readonly IVoiceCatalog voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));
    private readonly DurationAnalysisService durationAnalysisService = durationAnalysisService ?? new DurationAnalysisService();
    private readonly TtsTimingOptions timingOptions = (timingOptions ?? TtsTimingOptions.Default).Normalize();
    private readonly IReferenceClipTrimmer referenceClipTrimmer = referenceClipTrimmer ?? throw new ArgumentNullException(nameof(referenceClipTrimmer));

    public Task GenerateTtsForSpeakerAsync(
        TranscriptProjectState currentState,
        GenerateTtsForSpeakerRequest request,
        CancellationToken cancellationToken) =>
        RunTtsForSpeakerAsync(
            currentState,
            request.SpeakerId,
            segmentIndices: null,
            voiceAssignmentOverride: null,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloning,
            request.PreferredExecutionProvider,
            request.RequirePreferredExecutionProvider,
            request.PreferredModelVariantAlias,
            cancellationToken);

    public async Task GenerateTtsForSegmentAsync(
        TranscriptProjectState currentState,
        GenerateTtsForSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Segment TTS was based on an out-of-date transcript revision.");

        TranscriptSegment segment = currentState.TranscriptSegments.FirstOrDefault(candidate => candidate.Id == request.SegmentId)
            ?? throw new InvalidOperationException("The selected segment was not found in the current transcript revision.");
        Guid speakerId = segment.SpeakerId
            ?? throw new InvalidOperationException("Assign the segment to a speaker before generating TTS.");

        await RunTtsForSpeakerAsync(
            currentState,
            speakerId,
            new HashSet<int> { segment.SegmentIndex },
            voiceAssignmentOverride: null,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloning,
            request.PreferredExecutionProvider,
            request.RequirePreferredExecutionProvider,
            request.PreferredModelVariantAlias,
            cancellationToken).ConfigureAwait(false);
    }

    public Task GenerateTtsForAllSpeakersAsync(
        TranscriptProjectState currentState,
        GenerateTtsForAllSpeakersRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null) =>
        GenerateTtsForAllSpeakersAsync(currentState, request, cancellationToken, progress, prefetch: null);

    internal async Task GenerateTtsForAllSpeakersAsync(
        TranscriptProjectState currentState,
        GenerateTtsForAllSpeakersRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress,
        TtsStreamingPrefetch? prefetch)
    {
        DateTimeOffset progressStartedAt = DateTimeOffset.UtcNow;
        PipelineProgressReporter.Started(progress, StageNames.Tts, phase: "Preparing speakers");
        try
        {
            Dictionary<Guid, VoiceAssignment> assignmentsBySpeakerId = currentState.VoiceAssignments
                .Where(assignment => !assignment.IsFallback)
                .ToDictionary(assignment => assignment.SpeakerId);
            HashSet<string> reservedStockVoiceIds = CollectReservedStockVoiceIds(currentState, request);
            ProjectSpeaker[] speakers = currentState.Speakers
                .OrderBy(speaker => speaker.CreatedAtUtc)
                .ToArray();

            for (int index = 0; index < speakers.Length; index++)
            {
                ProjectSpeaker speaker = speakers[index];
                string speakerLabel = BuildSpeakerProgressLabel(speaker, index + 1, speakers.Length);
                PipelineProgressReporter.Determinate(
                    progress,
                    StageNames.Tts,
                    index,
                    speakers.Length,
                    "Preparing speaker",
                    speakerLabel,
                    currentItemLabel: speaker.DisplayName);
                bool hasAssignedSegments = currentState.TranscriptSegments
                    .Any(segment => segment.SpeakerId == speaker.Id);
                if (!hasAssignedSegments)
                {
                    PipelineProgressReporter.Phase(
                        progress,
                        StageNames.Tts,
                        "Skipping speaker",
                        $"{speaker.DisplayName} has no assigned transcript segments.");
                    continue;
                }

                TranslationRevision translationRevision = currentState.CurrentTranslationRevision
                    ?? throw new InvalidOperationException("Generate or load a translation before starting TTS.");
                // A streaming prefetch resolved this speaker before translation; reuse that plan so
                // the stage renders with exactly the voice the prefetched clips were made with.
                TtsSpeakerPlan plan = prefetch is not null && prefetch.SpeakerPlans.TryGetValue(speaker.Id, out TtsSpeakerPlan? prefetchedPlan)
                    ? prefetchedPlan
                    : await ResolveAllSpeakersPlanAsync(
                        currentState,
                        speaker,
                        translationRevision.TargetLanguage,
                        request,
                        assignmentsBySpeakerId,
                        reservedStockVoiceIds,
                        cancellationToken,
                        progress).ConfigureAwait(false);
                await RunSpeakerPlanAsync(
                    currentState,
                    speaker.Id,
                    segmentIndices: null,
                    plan,
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider,
                    request.PreferredModelVariantAlias,
                    cancellationToken,
                    progress,
                    prefetch).ConfigureAwait(false);
            }

            PipelineProgressReporter.Completed(
                progress,
                StageNames.Tts,
                DateTimeOffset.UtcNow - progressStartedAt,
                "TTS finished.");
        }
        catch (OperationCanceledException)
        {
            PipelineProgressReporter.Failed(
                progress,
                StageNames.Tts,
                "TTS canceled.",
                DateTimeOffset.UtcNow - progressStartedAt);
            throw;
        }
        catch (Exception ex)
        {
            PipelineProgressReporter.Failed(
                progress,
                StageNames.Tts,
                ex.Message,
                DateTimeOffset.UtcNow - progressStartedAt);
            throw;
        }
    }

    public async Task<PreviewVoiceResult> PreviewVoiceAsync(
        PreviewVoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SampleText))
        {
            throw new InvalidOperationException("Voice preview text is required.");
        }

        if (!voiceCatalog.TryGetVoice(request.VoiceId, out VoiceCatalogEntry? voice))
        {
            throw new InvalidOperationException($"Voicepack '{request.VoiceId}' is not available.");
        }

        string languageCode = string.IsNullOrWhiteSpace(request.LanguageCode)
            ? voice.LanguageCode
            : request.LanguageCode.Trim();
        string? trimmedAlias = request.PreferredModelAlias?.Trim();
        bool isQwen3PresetVoice = Qwen3TtsDefaults.IsPresetVoiceId(voice.VoiceId);
        string preferredAlias = isQwen3PresetVoice && !Qwen3TtsDefaults.IsCustomVoiceAlias(trimmedAlias)
            ? Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null)
            : string.IsNullOrWhiteSpace(trimmedAlias)
                ? StockTtsDefaults.KokoroPrimaryAlias
                : trimmedAlias;
        TtsSynthesisResult result = await ttsEngine.SynthesizeAsync(
            new TtsSynthesisRequest(
                request.SampleText.Trim(),
                languageCode,
                voice,
                Options: new InferenceRequestOptions(
                    preferredAlias,
                    RequirePreferredModelAlias: true,
                    PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
                    RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                    PreferredModelVariantAlias: request.PreferredModelVariantAlias)),
            cancellationToken).ConfigureAwait(false);

        return new PreviewVoiceResult(
            result.WavBytes,
            result.SampleRate,
            result.ModelId,
            result.VoiceId,
            result.Provider);
    }

    public async Task RegenerateStaleTtsForSpeakerAsync(
        TranscriptProjectState currentState,
        RegenerateStaleTtsForSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        HashSet<int> speakerSegmentIndices = currentState.TranscriptSegments
            .Where(segment => segment.SpeakerId == request.SpeakerId)
            .Select(segment => segment.SegmentIndex)
            .ToHashSet();
        HashSet<int> staleSegmentIndices = currentState.TtsSegmentStates
            .Where(state => state.IsStale && speakerSegmentIndices.Contains(state.SegmentIndex))
            .Select(state => state.SegmentIndex)
            .ToHashSet();
        if (staleSegmentIndices.Count == 0)
        {
            return;
        }

        await RunTtsForSpeakerAsync(
            currentState,
            request.SpeakerId,
            staleSegmentIndices,
            voiceAssignmentOverride: null,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloning,
            request.PreferredExecutionProvider,
            request.RequirePreferredExecutionProvider,
            request.PreferredModelVariantAlias,
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StretchTtsTakeAsync(
        TranscriptProjectState currentState,
        StretchTtsTakeRequest request,
        CancellationToken cancellationToken)
    {
        if (audioTimeStretchService is null)
        {
            throw new InvalidOperationException("Audio time stretching is not configured.");
        }

        TtsTake take = currentState.TtsTakes.FirstOrDefault(candidate => candidate.Id == request.TakeId)
            ?? throw new InvalidOperationException("The selected TTS take was not found.");
        if (take.Status is not TtsTakeStatus.Completed || take.IsStale)
        {
            throw new InvalidOperationException("Only a fresh completed TTS take can be stretched.");
        }

        TranscriptSegment sourceSegment = currentState.TranscriptSegments
            .FirstOrDefault(segment => segment.SegmentIndex == take.SegmentIndex)
            ?? throw new InvalidOperationException("The source segment for this TTS take was not found.");
        ProjectArtifact artifact = take.ArtifactId is Guid artifactId
            ? currentState.ProjectState.Artifacts.FirstOrDefault(candidate => candidate.Id == artifactId)
                ?? throw new InvalidOperationException("The TTS artifact for this take was not found.")
            : throw new InvalidOperationException("The TTS take does not reference an artifact.");
        int sampleRate = take.SampleRate ?? artifact.SampleRate ?? throw new InvalidOperationException("The TTS take sample rate is missing.");
        double? currentDurationSeconds = artifact.DurationSeconds ??
                                         (take.DurationSamples is int durationSamples && sampleRate > 0
                                             ? (double)durationSamples / sampleRate
                                             : null);
        double preStretchDurationSeconds = currentDurationSeconds
            ?? throw new InvalidOperationException("The TTS take duration is missing.");
        DurationAnalysisResult analysis = durationAnalysisService.Analyze(
            sourceSegment,
            currentDurationSeconds,
            timingOptions);
        if (!analysis.IsStretchable || analysis.TempoRatio is not double tempoRatio)
        {
            throw new InvalidOperationException("This TTS take is too short or too extreme to stretch cleanly.");
        }

        if (Math.Abs(tempoRatio - 1d) < 0.001d)
        {
            throw new InvalidOperationException("This TTS take already matches the source segment duration.");
        }

        string finalPath = artifactStore.GetPath(artifact.RelativePath);
        if (!File.Exists(finalPath))
        {
            throw new InvalidOperationException("The TTS artifact file is missing.");
        }

        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(artifact.RelativePath));
        AudioTimeStretchResult stretchResult = await audioTimeStretchService.StretchAsync(
            new AudioTimeStretchRequest(
                finalPath,
                tx.TemporaryPath,
                tempoRatio,
                timingOptions.EnableRubberbandStretch,
                timingOptions.RubberbandStretchThreshold),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);

        FileFingerprint fingerprint = await fileFingerprintService
            .ComputeAsync(finalPath, cancellationToken)
            .ConfigureAwait(false);
        ProjectArtifact updatedArtifact = artifact with
        {
            Sha256 = fingerprint.Sha256,
            SizeBytes = fingerprint.SizeBytes,
            DurationSeconds = analysis.OriginalDurationSeconds,
            SampleRate = sampleRate,
            ChannelCount = artifact.ChannelCount ?? 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Provenance = AppendStretchProvenance(artifact.Provenance, TtsStretchMode.Manual, stretchResult.Engine)
        };
        await mediaAssetRepository.SaveArtifactAsync(updatedArtifact, cancellationToken).ConfigureAwait(false);

        int stretchedDurationSamples = Math.Max(1, (int)Math.Round(analysis.OriginalDurationSeconds * sampleRate));
        TtsTake updatedTake = take.ApplyStretch(
            TtsStretchMode.Manual,
            stretchResult.Engine,
            tempoRatio,
            preStretchDurationSeconds,
            stretchedDurationSamples,
            analysis.OverrunRatio);
        await ttsTakeRepository.SaveAsync(updatedTake, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<TtsSegmentState> BuildTtsSegmentStates(
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<TranslatedSegment> translatedSegments,
        IReadOnlyList<TtsTake> ttsTakes,
        IReadOnlyList<ProjectArtifact> artifacts,
        IReadOnlyList<StageRunRecord> stageRuns)
    {
        Dictionary<int, TranscriptSegment> sourceSegmentsByIndex = transcriptSegments
            .ToDictionary(segment => segment.SegmentIndex);
        Dictionary<Guid, ProjectArtifact> artifactsById = artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.TtsTake)
            .ToDictionary(artifact => artifact.Id);
        Dictionary<int, TtsTake> latestTakesBySegmentIndex = ttsTakes
            .GroupBy(take => take.SegmentIndex)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(take => take.CreatedAtUtc)
                    .First());
        Dictionary<Guid, StageRunRecord> stageRunsById = stageRuns
            .ToDictionary(static run => run.Id);

        return translatedSegments
            .OrderBy(segment => segment.SegmentIndex)
            .Select(segment =>
            {
                sourceSegmentsByIndex.TryGetValue(segment.SegmentIndex, out TranscriptSegment? sourceSegment);
                double? originalDurationSeconds = sourceSegment is null
                    ? null
                    : sourceSegment.EndSeconds - sourceSegment.StartSeconds;
                if (!latestTakesBySegmentIndex.TryGetValue(segment.SegmentIndex, out TtsTake? take))
                {
                    return new TtsSegmentState(
                        segment.SegmentIndex,
                        TakeId: null,
                        ArtifactRelativePath: null,
                        Status: null,
                        IsStale: false,
                        DurationSeconds: null,
                        DurationOverrunRatio: null,
                        HasDurationWarning: false,
                        WarningMessage: null,
                        OriginalDurationSeconds: originalDurationSeconds);
                }

                bool isStale = IsTakeStale(take, segment);
                ProjectArtifact? artifact = take.ArtifactId is Guid artifactId &&
                                            artifactsById.TryGetValue(artifactId, out ProjectArtifact? storedArtifact)
                    ? storedArtifact
                    : null;
                double? durationSeconds = ComputeTakeDurationSeconds(take, artifact);
                var (severity, hasDurationWarning, hasSpeedLimitWarning, canManualStretch) =
                    ComputeDurationAnalysis(sourceSegment, durationSeconds, isStale, take, artifact);
                StageRunRecord? stageRun = take.StageRunId is Guid stageRunId &&
                                           stageRunsById.TryGetValue(stageRunId, out StageRunRecord? linkedRun)
                    ? linkedRun
                    : null;
                return new TtsSegmentState(
                    segment.SegmentIndex,
                    take.Id,
                    artifact?.RelativePath,
                    take.Status,
                    isStale,
                    durationSeconds,
                    take.DurationOverrunRatio,
                    hasDurationWarning || hasSpeedLimitWarning,
                    BuildTtsWarningMessage(hasDurationWarning, hasSpeedLimitWarning),
                    originalDurationSeconds,
                    take.PreStretchDurationSeconds,
                    take.StretchRatioApplied,
                    take.StretchMode,
                    take.StretchEngine,
                    severity,
                    hasSpeedLimitWarning,
                    canManualStretch,
                    take.Provider,
                    take.ModelId,
                    stageRun?.RuntimeInfo?.ModelAlias,
                    stageRun?.RuntimeInfo?.ModelVariant);
            })
            .ToArray();
    }

    private async Task RunTtsForSpeakerAsync(
        TranscriptProjectState currentState,
        Guid speakerId,
        IReadOnlySet<int>? segmentIndices,
        VoiceAssignment? voiceAssignmentOverride,
        string? preferredModelAlias,
        bool useReferenceClipForVoiceCloning,
        ExecutionProviderKind? preferredExecutionProvider,
        bool requirePreferredExecutionProvider,
        string? preferredModelVariantAlias,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null,
        HashSet<string>? reservedStockVoiceIds = null)
    {
        ProjectSpeaker speaker = currentState.Speakers.FirstOrDefault(speaker => speaker.Id == speakerId)
            ?? throw new InvalidOperationException("The selected speaker was not found.");

        TranslationRevision translationRevision = currentState.CurrentTranslationRevision
            ?? throw new InvalidOperationException("Generate or load a translation before starting TTS.");
        TtsSpeakerPlan plan = await ResolveSpeakerPlanAsync(
            currentState,
            speaker,
            translationRevision.TargetLanguage,
            voiceAssignmentOverride,
            preferredModelAlias,
            useReferenceClipForVoiceCloning,
            cancellationToken,
            progress,
            reservedStockVoiceIds).ConfigureAwait(false);
        await RunSpeakerPlanAsync(
            currentState,
            speakerId,
            segmentIndices,
            plan,
            preferredExecutionProvider,
            requirePreferredExecutionProvider,
            preferredModelVariantAlias,
            cancellationToken,
            progress,
            prefetch: null).ConfigureAwait(false);
    }

    private async Task<TtsSpeakerPlan> ResolveSpeakerPlanAsync(
        TranscriptProjectState currentState,
        ProjectSpeaker speaker,
        string targetLanguage,
        VoiceAssignment? voiceAssignmentOverride,
        string? preferredModelAlias,
        bool useReferenceClipForVoiceCloning,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress,
        HashSet<string>? reservedStockVoiceIds)
    {
        Guid speakerId = speaker.Id;
        VoiceAssignment? persistedAssignment = voiceAssignmentOverride ??
            currentState.VoiceAssignments.FirstOrDefault(candidate => candidate.SpeakerId == speakerId && !candidate.IsFallback);
        VoiceAssignment assignment = persistedAssignment ??
            (useReferenceClipForVoiceCloning
                ? VoiceAssignment.Create(
                    currentState.ProjectState.Project.Id,
                    speakerId,
                    VoiceCloningDefaults.ChatterboxPrimaryAlias,
                    requiresConsent: true)
                : throw new InvalidOperationException("Assign a Kokoro voicepack to the speaker before starting TTS."));
        IReadOnlyList<ProjectArtifact> projectArtifacts = currentState.ProjectState.Artifacts;
        if (useReferenceClipForVoiceCloning)
        {
            PreparedReferenceClip preparedReferenceClip = await EnsureReferenceClipForVoiceCloningAsync(
                currentState,
                assignment,
                cancellationToken).ConfigureAwait(false);
            if (preparedReferenceClip.InsufficientSpeechAnalysis is { } insufficientSpeech)
            {
                assignment = await FallBackShortCloneToStockTtsAsync(
                    currentState,
                    speaker,
                    assignment,
                    targetLanguage,
                    insufficientSpeech,
                    reservedStockVoiceIds,
                    cancellationToken).ConfigureAwait(false);
                projectArtifacts = currentState.ProjectState.Artifacts;
                useReferenceClipForVoiceCloning = false;
                preferredModelAlias = assignment.VoiceModelId;

                PipelineProgressReporter.Phase(
                    progress,
                    StageNames.Tts,
                    "Using stock voice",
                    $"{speaker.DisplayName}: clone reference had {insufficientSpeech.ActiveSpeechSeconds:F2}s of active speech (need {ReferenceClipPolicy.MinimumActiveSpeechSeconds:F1}s). Using {DescribeStockFallback(assignment)}.");
            }
            else
            {
                assignment = preparedReferenceClip.Assignment;
                projectArtifacts = preparedReferenceClip.ProjectArtifacts;
            }
        }
        else if (IsCloneOnlyStockUnresolvable(assignment))
        {
            // A prior voice-clone run persisted a non-fallback clone assignment
            // (VoiceModelId is a clone-model alias such as chatterbox-turbo-onnx). This is a
            // non-clone run, so StartTtsStageHandler would attempt a stock voicepack lookup
            // against that clone alias and throw "Voicepack '...' is not available." Route it
            // to a valid stock voice for the target language instead.
            VoiceAssignment substituteAssignment = await SubstituteStockVoiceForCloneOnlyAssignmentAsync(
                currentState,
                speaker,
                assignment,
                targetLanguage,
                reservedStockVoiceIds,
                cancellationToken).ConfigureAwait(false);
            preferredModelAlias = StockTtsVoiceMatcher.SupportsKokoro(targetLanguage)
                ? substituteAssignment.VoiceModelId
                : null;
            assignment = substituteAssignment;

            PipelineProgressReporter.Phase(
                progress,
                StageNames.Tts,
                "Using stock voice",
                $"{speaker.DisplayName}: persisted voice-clone model is not available for a non-clone run. Using {DescribeStockFallback(assignment)}.");
        }

        return new TtsSpeakerPlan(assignment, preferredModelAlias, projectArtifacts, useReferenceClipForVoiceCloning);
    }

    private async Task RunSpeakerPlanAsync(
        TranscriptProjectState currentState,
        Guid speakerId,
        IReadOnlySet<int>? segmentIndices,
        TtsSpeakerPlan plan,
        ExecutionProviderKind? preferredExecutionProvider,
        bool requirePreferredExecutionProvider,
        string? preferredModelVariantAlias,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress,
        TtsStreamingPrefetch? prefetch)
    {
        ProjectSpeaker speaker = currentState.Speakers.FirstOrDefault(speaker => speaker.Id == speakerId)
            ?? throw new InvalidOperationException("The selected speaker was not found.");
        TranslationRevision translationRevision = currentState.CurrentTranslationRevision
            ?? throw new InvalidOperationException("Generate or load a translation before starting TTS.");
        if (currentState.TranslatedSegments.Count == 0)
        {
            throw new InvalidOperationException("The current translation revision has no translated segments.");
        }

        PipelineProgressReporter.Phase(
            progress,
            StageNames.Tts,
            "Preparing speaker",
            currentItemLabel: speaker.DisplayName);
        await startTtsStageHandler.HandleAsync(
            CreateStageRequest(
                currentState,
                speakerId,
                translationRevision.TargetLanguage,
                plan,
                currentState.TranslatedSegments,
                segmentIndices,
                preferredExecutionProvider,
                requirePreferredExecutionProvider,
                preferredModelVariantAlias),
            cancellationToken,
            progress,
            prefetch).ConfigureAwait(false);
    }

    private static StartTtsStageRequest CreateStageRequest(
        TranscriptProjectState currentState,
        Guid speakerId,
        string targetLanguage,
        TtsSpeakerPlan plan,
        IReadOnlyList<TranslatedSegment> translatedSegments,
        IReadOnlySet<int>? segmentIndices,
        ExecutionProviderKind? preferredExecutionProvider,
        bool requirePreferredExecutionProvider,
        string? preferredModelVariantAlias) =>
        new(
            currentState.ProjectState.Project.Id,
            TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
            speakerId,
            targetLanguage,
            plan.Assignment,
            currentState.TranscriptSegments,
            translatedSegments,
            segmentIndices,
            plan.PreferredModelAlias,
            plan.ProjectArtifacts,
            plan.UseReferenceClipForVoiceCloning,
            preferredExecutionProvider,
            requirePreferredExecutionProvider,
            preferredModelVariantAlias);

    // An explicit --voice override in the request must take precedence over any previously
    // persisted non-fallback assignment (e.g. on a resume run). Only when the request carries no
    // explicit override for the speaker do we honor the pre-existing assignment, falling back to
    // the request path otherwise.
    private async Task<TtsSpeakerPlan> ResolveAllSpeakersPlanAsync(
        TranscriptProjectState currentState,
        ProjectSpeaker speaker,
        string targetLanguage,
        GenerateTtsForAllSpeakersRequest request,
        IReadOnlyDictionary<Guid, VoiceAssignment> assignmentsBySpeakerId,
        HashSet<string> reservedStockVoiceIds,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress)
    {
        VoiceAssignment? assignment;
        if (RequestHasExplicitVoiceOverride(request, speaker.Id))
        {
            assignment = await TryPersistRequestedVoiceAssignmentAsync(
                currentState,
                speaker.Id,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            assignmentsBySpeakerId.TryGetValue(speaker.Id, out assignment);
            if (assignment is null)
            {
                assignment = await TryPersistRequestedVoiceAssignmentAsync(
                    currentState,
                    speaker.Id,
                    request,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return await ResolveSpeakerPlanAsync(
            currentState,
            speaker,
            targetLanguage,
            assignment,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloningBySpeakerId?.TryGetValue(speaker.Id, out bool useReferenceClipForVoiceCloning) == true
                && useReferenceClipForVoiceCloning,
            cancellationToken,
            progress,
            reservedStockVoiceIds).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves every speaker's TTS plan before translation runs and returns a prefetch that renders
    /// stock-voice segments as the translation stream produces them. Pass it to translation as the
    /// segment observer and to the TTS stage, which reuses these plans and claims the clips.
    /// </summary>
    internal async Task<TtsStreamingPrefetch> CreateStreamingPrefetchAsync(
        TranscriptProjectState currentState,
        GenerateTtsForAllSpeakersRequest request,
        string targetLanguage,
        IApplicationLogger? logger,
        CancellationToken cancellationToken,
        CancellationToken runCancellationToken)
    {
        string normalizedTargetLanguage = targetLanguage.Trim().ToLowerInvariant();
        Dictionary<Guid, VoiceAssignment> assignmentsBySpeakerId = currentState.VoiceAssignments
            .Where(assignment => !assignment.IsFallback)
            .ToDictionary(assignment => assignment.SpeakerId);
        HashSet<string> reservedStockVoiceIds = CollectReservedStockVoiceIds(currentState, request);
        var plans = new Dictionary<Guid, TtsSpeakerPlan>();
        var speakersBySegmentIndex = new Dictionary<int, TtsPrefetchSpeaker>();
        int? maxConcurrency = null;
        foreach (ProjectSpeaker speaker in currentState.Speakers.OrderBy(speaker => speaker.CreatedAtUtc))
        {
            if (!currentState.TranscriptSegments.Any(segment => segment.SpeakerId == speaker.Id))
            {
                continue;
            }

            TtsSpeakerPlan plan = await ResolveAllSpeakersPlanAsync(
                currentState,
                speaker,
                normalizedTargetLanguage,
                request,
                assignmentsBySpeakerId,
                reservedStockVoiceIds,
                cancellationToken,
                progress: null).ConfigureAwait(false);
            plans[speaker.Id] = plan;

            (TtsPrefetchSpeaker Speaker, int MaxConcurrency)? prepared;
            try
            {
                prepared = await startTtsStageHandler.PreparePrefetchSpeakerAsync(
                    CreateStageRequest(
                        currentState,
                        speaker.Id,
                        normalizedTargetLanguage,
                        plan,
                        translatedSegments: [],
                        segmentIndices: null,
                        request.PreferredExecutionProvider,
                        request.RequirePreferredExecutionProvider,
                        request.PreferredModelVariantAlias),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // The TTS stage reports this speaker's voice problem; prefetch just skips it.
                logger?.LogWarning($"TTS prefetch skipped speaker {speaker.DisplayName}: {ex.Message}");
                continue;
            }

            if (prepared is not { } speakerPrefetch)
            {
                continue;
            }

            // One shared pool renders every speaker, so it must honour the tightest per-speaker
            // bound: a large-model GPU speaker's VRAM cap would otherwise be exceeded.
            maxConcurrency = Math.Min(maxConcurrency ?? int.MaxValue, speakerPrefetch.MaxConcurrency);
            foreach (int segmentIndex in speakerPrefetch.Speaker.SourceSegmentsByIndex.Keys)
            {
                speakersBySegmentIndex[segmentIndex] = speakerPrefetch.Speaker;
            }
        }

        return new TtsStreamingPrefetch(
            startTtsStageHandler,
            speakersBySegmentIndex,
            plans,
            maxConcurrency ?? 1,
            logger,
            runCancellationToken);
    }

    private static bool RequestHasExplicitVoiceOverride(GenerateTtsForAllSpeakersRequest request, Guid speakerId) =>
        request.VoiceIdsBySpeakerId is not null &&
        request.VoiceIdsBySpeakerId.TryGetValue(speakerId, out string? explicitVoiceId) &&
        !string.IsNullOrWhiteSpace(explicitVoiceId);

    private async Task<VoiceAssignment?> TryPersistRequestedVoiceAssignmentAsync(
        TranscriptProjectState currentState,
        Guid speakerId,
        GenerateTtsForAllSpeakersRequest request,
        CancellationToken cancellationToken)
    {
        if (request.VoiceIdsBySpeakerId is not null &&
            request.VoiceIdsBySpeakerId.TryGetValue(speakerId, out string? explicitVoiceId) &&
            !string.IsNullOrWhiteSpace(explicitVoiceId))
        {
            string normalizedVoiceId = explicitVoiceId.Trim();
            if (!voiceCatalog.TryGetVoice(normalizedVoiceId, out _))
            {
                throw new InvalidOperationException($"Voicepack '{normalizedVoiceId}' is not available.");
            }

            VoiceAssignment? existing = await voiceAssignmentRepository.GetAsync(
                currentState.ProjectState.Project.Id,
                speakerId,
                cancellationToken).ConfigureAwait(false);
            VoiceAssignment assignment = existing is null
                ? VoiceAssignment.Create(
                    currentState.ProjectState.Project.Id,
                    speakerId,
                    ResolveVoiceModelId(normalizedVoiceId),
                    normalizedVoiceId)
                : existing with
                {
                    VoiceModelId = ResolveVoiceModelId(normalizedVoiceId),
                    VoiceVariant = normalizedVoiceId,
                    RequiresConsent = false,
                    IsFallback = false,
                    ReferenceClipArtifactId = existing.ReferenceClipArtifactId
                };
            await voiceAssignmentRepository.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
            return assignment;
        }

        if (request.FallbackVoiceIdsBySpeakerId is not null &&
            request.FallbackVoiceIdsBySpeakerId.TryGetValue(speakerId, out string? fallbackVoiceId) &&
            !string.IsNullOrWhiteSpace(fallbackVoiceId))
        {
            string normalizedVoiceId = fallbackVoiceId.Trim();
            if (!voiceCatalog.TryGetVoice(normalizedVoiceId, out _))
            {
                throw new InvalidOperationException($"Voicepack '{normalizedVoiceId}' is not available.");
            }

            // Always mint a fresh fallback assignment. voiceAssignmentRepository.GetAsync only
            // ever returns NON-fallback rows, so reusing an existing row's primary-key Id for a
            // fallback (is_fallback=1) row does not match the non-fallback partial-unique upsert
            // conflict target and would collide with that row's PRIMARY KEY on a real SQLite-backed
            // resume. CreateFallback allocates a new Id, so the fallback row inserts cleanly.
            VoiceAssignment assignment = VoiceAssignment.CreateFallback(
                currentState.ProjectState.Project.Id,
                speakerId,
                ResolveVoiceModelId(normalizedVoiceId),
                normalizedVoiceId);
            await voiceAssignmentRepository.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
            return assignment;
        }

        return null;
    }

    private static string ResolveVoiceModelId(string voiceId) =>
        Qwen3TtsDefaults.IsPresetVoiceId(voiceId)
            ? Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null)
            : StockTtsDefaults.KokoroPrimaryAlias;

    private static string BuildSpeakerProgressLabel(ProjectSpeaker speaker, int speakerNumber, int speakerCount) =>
        string.IsNullOrWhiteSpace(speaker.DisplayName)
            ? $"Speaker {speakerNumber} of {speakerCount}"
            : $"Speaker {speakerNumber} of {speakerCount}: {speaker.DisplayName}";

    private async Task<PreparedReferenceClip> EnsureReferenceClipForVoiceCloningAsync(
        TranscriptProjectState currentState,
        VoiceAssignment assignment,
        CancellationToken cancellationToken)
    {
        ProjectArtifact? existingReferenceArtifact = await ResolveAssignedReferenceArtifactAsync(
            currentState,
            assignment.ReferenceClipArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (IsManualReferenceClip(existingReferenceArtifact))
        {
            return new PreparedReferenceClip(
                assignment,
                IncludeProjectArtifact(currentState.ProjectState.Artifacts, existingReferenceArtifact!));
        }

        ProjectArtifact? sourceArtifact = TryResolveReferenceClipSourceAudioArtifact(currentState);
        if (sourceArtifact is null)
        {
            if (existingReferenceArtifact is not null)
            {
                return new PreparedReferenceClip(
                    assignment,
                    IncludeProjectArtifact(currentState.ProjectState.Artifacts, existingReferenceArtifact));
            }

            throw new InvalidOperationException("Normalized audio is required for automatic voice clone reference capture.");
        }

        AutoReferenceClipPlan singlePlan = BuildAutoReferenceClipPlan(
            currentState,
            assignment.SpeakerId,
            sourceArtifact,
            mode: "single");
        AutoReferenceClipPlan packedPlan = BuildAutoReferenceClipPlan(
            currentState,
            assignment.SpeakerId,
            sourceArtifact,
            mode: "packed");
        if (existingReferenceArtifact is not null &&
            (IsCurrentAutoReferenceClip(existingReferenceArtifact, singlePlan.Fingerprint) ||
             IsCurrentAutoReferenceClip(existingReferenceArtifact, packedPlan.Fingerprint)) &&
            File.Exists(artifactStore.GetPath(existingReferenceArtifact.RelativePath)))
        {
            return new PreparedReferenceClip(
                assignment,
                IncludeProjectArtifact(currentState.ProjectState.Artifacts, existingReferenceArtifact));
        }

        if (audioClipExtractor is null)
        {
            throw new InvalidOperationException("Automatic voice clone reference capture is not configured.");
        }

        if (referenceClipAnalyzer is null)
        {
            throw new InvalidOperationException("Reference clip active-speech validation is not configured.");
        }

        AutoReferenceCaptureResult capture = await CaptureAutoReferenceClipAsync(
            currentState,
            assignment.SpeakerId,
            singlePlan,
            cancellationToken).ConfigureAwait(false);
        if (capture.Artifact is null)
        {
            return new PreparedReferenceClip(
                assignment,
                currentState.ProjectState.Artifacts,
                capture.Analysis);
        }

        ProjectArtifact referenceArtifact = capture.Artifact;
        VoiceAssignment updatedAssignment = await AssignReferenceClipArtifactAsync(
            currentState.ProjectState.Project.Id,
            assignment,
            referenceArtifact.Id,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectArtifact> refreshedArtifacts = await mediaAssetRepository
            .GetArtifactsAsync(currentState.ProjectState.Project.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!refreshedArtifacts.Any(artifact => artifact.Id == referenceArtifact.Id))
        {
            refreshedArtifacts = currentState.ProjectState.Artifacts
                .Where(artifact => artifact.Id != referenceArtifact.Id)
                .Concat([referenceArtifact])
                .ToArray();
        }

        return new PreparedReferenceClip(updatedAssignment, refreshedArtifacts);
    }

    private async Task<ProjectArtifact?> ResolveAssignedReferenceArtifactAsync(
        TranscriptProjectState currentState,
        Guid? referenceClipArtifactId,
        CancellationToken cancellationToken)
    {
        if (referenceClipArtifactId is not Guid artifactId)
        {
            return null;
        }

        ProjectArtifact? stateArtifact = currentState.ProjectState.Artifacts
            .FirstOrDefault(artifact => artifact.Id == artifactId && artifact.Kind == ArtifactKind.ReferenceClip);
        if (stateArtifact is not null)
        {
            return stateArtifact;
        }

        IReadOnlyList<ProjectArtifact> repositoryArtifacts = await mediaAssetRepository
            .GetArtifactsAsync(currentState.ProjectState.Project.Id, cancellationToken)
            .ConfigureAwait(false);
        return repositoryArtifacts
            .FirstOrDefault(artifact => artifact.Id == artifactId && artifact.Kind == ArtifactKind.ReferenceClip);
    }

    private async Task<AutoReferenceCaptureResult> CaptureAutoReferenceClipAsync(
        TranscriptProjectState currentState,
        Guid speakerId,
        AutoReferenceClipPlan singlePlan,
        CancellationToken cancellationToken)
    {
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string relativePath = ProjectArtifactPaths.GetReferenceClipRelativePath(speakerId, now);
        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(relativePath));
        Guid? savedArtifactId = null;

        try
        {
            AutoReferenceClipExtraction extraction = await ExtractAndAnalyzeAutoReferenceAsync(
                singlePlan,
                tx.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            AutoReferenceClipPlan selectedPlan = singlePlan;
            if (extraction.Analysis.ActiveSpeechSeconds < ReferenceClipPolicy.MinimumActiveSpeechSeconds)
            {
                selectedPlan = BuildAutoReferenceClipPlan(
                    currentState,
                    speakerId,
                    singlePlan.SourceArtifact,
                    mode: "packed");
                extraction = await ExtractAndAnalyzeAutoReferenceAsync(
                    selectedPlan,
                    tx.TemporaryPath,
                    cancellationToken).ConfigureAwait(false);
            }

            if (extraction.Analysis.ActiveSpeechSeconds < ReferenceClipPolicy.MinimumActiveSpeechSeconds)
            {
                return new AutoReferenceCaptureResult(null, extraction.Analysis);
            }

            await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);
            FileFingerprint fingerprint = await fileFingerprintService
                .ComputeAsync(artifactStore.GetPath(relativePath), cancellationToken)
                .ConfigureAwait(false);
            var artifact = new ProjectArtifact(
                Guid.NewGuid(),
                currentState.ProjectState.Project.Id,
                mediaAsset.Id,
                ArtifactKind.ReferenceClip,
                relativePath,
                fingerprint.Sha256,
                fingerprint.SizeBytes,
                extraction.Analysis.TotalDurationSeconds,
                extraction.Analysis.SampleRate,
                extraction.Analysis.ChannelCount,
                now,
                StageRunId: null,
                Provenance: BuildAutoReferenceProvenance(speakerId, selectedPlan, extraction.Analysis));
            await mediaAssetRepository.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            savedArtifactId = artifact.Id;
            return new AutoReferenceCaptureResult(artifact, extraction.Analysis);
        }
        catch
        {
            await DeleteSavedReferenceClipArtifactBestEffortAsync(savedArtifactId).ConfigureAwait(false);
            DeleteCommittedReferenceClipFileBestEffort(relativePath);
            throw;
        }
    }

    private async Task<AutoReferenceClipExtraction> ExtractAndAnalyzeAutoReferenceAsync(
        AutoReferenceClipPlan plan,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        AudioClipExtractionResult extractionResult = await audioClipExtractor!.ExtractAsync(
            artifactStore.GetPath(plan.SourceArtifact.RelativePath),
            plan.Ranges,
            destinationPath,
            cancellationToken).ConfigureAwait(false);
        _ = await referenceClipTrimmer.TrimAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        ReferenceClipAnalysis analysis = await referenceClipAnalyzer!
            .AnalyzeAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);
        return new AutoReferenceClipExtraction(extractionResult, analysis);
    }

    private AutoReferenceClipPlan BuildAutoReferenceClipPlan(
        TranscriptProjectState state,
        Guid speakerId,
        ProjectArtifact sourceArtifact,
        string mode)
    {
        TranscriptSegment[] assignedSegments = state.TranscriptSegments
            .Where(segment => segment.SpeakerId == speakerId)
            .OrderBy(segment => segment.StartSeconds)
            .ToArray();
        if (assignedSegments.Length == 0)
        {
            throw new InvalidOperationException("No transcript segments are assigned to this speaker; assign speech before generating a voice-cloned dub.");
        }

        AudioClipRange[] ranges = string.Equals(mode, "packed", StringComparison.Ordinal)
            ? BuildPackedAutoReferenceRanges(assignedSegments)
            : [BuildSingleAutoReferenceRange(assignedSegments)];
        string fingerprint = BuildAutoReferenceFingerprint(
            state.CurrentTranscriptRevision?.Id,
            sourceArtifact,
            ranges,
            mode);
        return new AutoReferenceClipPlan(sourceArtifact, ranges, mode, fingerprint);
    }

    private static IReadOnlyList<ProjectArtifact> IncludeProjectArtifact(
        IReadOnlyList<ProjectArtifact> artifacts,
        ProjectArtifact artifact) =>
        artifacts.Any(candidate => candidate.Id == artifact.Id)
            ? artifacts
            : artifacts.Concat([artifact]).ToArray();

    private static AudioClipRange BuildSingleAutoReferenceRange(IReadOnlyList<TranscriptSegment> assignedSegments)
    {
        TranscriptSegment segment = assignedSegments
            .OrderByDescending(candidate => candidate.EndSeconds - candidate.StartSeconds)
            .ThenBy(candidate => candidate.StartSeconds)
            .First();
        double endSeconds = Math.Min(
            segment.EndSeconds,
            segment.StartSeconds + ReferenceClipPolicy.RecommendedMaximumActiveSpeechSeconds);
        return new AudioClipRange(segment.StartSeconds, endSeconds);
    }

    private static AudioClipRange[] BuildPackedAutoReferenceRanges(IReadOnlyList<TranscriptSegment> assignedSegments)
    {
        var ranges = new List<AudioClipRange>();
        double totalDurationSeconds = 0d;
        foreach (TranscriptSegment segment in assignedSegments)
        {
            double availableDurationSeconds = segment.EndSeconds - segment.StartSeconds;
            if (availableDurationSeconds <= 0d)
            {
                continue;
            }

            double remainingDurationSeconds = ReferenceClipPolicy.RecommendedMaximumActiveSpeechSeconds - totalDurationSeconds;
            if (remainingDurationSeconds <= 0d)
            {
                break;
            }

            double durationSeconds = Math.Min(availableDurationSeconds, remainingDurationSeconds);
            ranges.Add(new AudioClipRange(segment.StartSeconds, segment.StartSeconds + durationSeconds));
            totalDurationSeconds += durationSeconds;
        }

        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("No usable transcript segment range is available for automatic voice clone reference capture.");
        }

        return ranges.ToArray();
    }

    private static ProjectArtifact? TryResolveReferenceClipSourceAudioArtifact(TranscriptProjectState state)
    {
        ProjectArtifact? acceptedVocalStem = TranscriptWorkflowUtilities.GetLatestAcceptedVocalStem(state.ProjectState.Artifacts, state.StageRuns);
        ProjectArtifact? routedArtifact = state.ProjectState.Artifacts
            .FirstOrDefault(artifact => string.Equals(
                artifact.RelativePath,
                state.AsrAudioRelativePath,
                StringComparison.OrdinalIgnoreCase));
        if (routedArtifact is { Kind: ArtifactKind.Vocals } &&
            acceptedVocalStem is not null &&
            string.Equals(routedArtifact.RelativePath, acceptedVocalStem.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return routedArtifact;
        }

        if (routedArtifact is { Kind: ArtifactKind.SpeechEnhancedAudio } ||
            (routedArtifact is { Kind: ArtifactKind.SpeechProcessedAudio } && acceptedVocalStem is not null))
        {
            return routedArtifact;
        }

        if (acceptedVocalStem is not null)
        {
            return acceptedVocalStem;
        }

        return TranscriptWorkflowUtilities.GetLatestArtifactByKind(state.ProjectState.Artifacts, ArtifactKind.NormalizedAudio);
    }

    private async Task<VoiceAssignment> AssignReferenceClipArtifactAsync(
        Guid projectId,
        VoiceAssignment existing,
        Guid referenceClipArtifactId,
        CancellationToken cancellationToken)
    {
        VoiceAssignment assignment = existing with
        {
            RequiresConsent = true,
            ReferenceClipArtifactId = referenceClipArtifactId
        };

        await voiceAssignmentRepository.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
        if (existing.ReferenceClipArtifactId != referenceClipArtifactId)
        {
            await ttsTakeRepository
                .MarkByVoiceAssignmentStaleAsync(projectId, assignment.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return assignment;
    }

    private async Task DeleteSavedReferenceClipArtifactBestEffortAsync(Guid? artifactId)
    {
        if (artifactId is not Guid savedArtifactId)
        {
            return;
        }

        try
        {
            await mediaAssetRepository.DeleteArtifactAsync(savedArtifactId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original reference-clip failure; cleanup errors should not replace it.
        }
    }

    private void DeleteCommittedReferenceClipFileBestEffort(string relativePath)
    {
        try
        {
            string committedPath = artifactStore.GetPath(relativePath);
            if (File.Exists(committedPath))
            {
                File.Delete(committedPath);
            }
        }
        catch
        {
            // Preserve the original reference-clip failure; cleanup errors should not replace it.
        }
    }

    private static bool IsManualReferenceClip(ProjectArtifact? artifact) =>
        artifact?.Provenance?.StartsWith("manual-speaker-reference:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCurrentAutoReferenceClip(ProjectArtifact artifact, string expectedFingerprint) =>
        artifact.Provenance?.Contains("auto-speaker-reference:v2", StringComparison.OrdinalIgnoreCase) == true &&
        artifact.Provenance.Contains($"fingerprint:{expectedFingerprint}", StringComparison.OrdinalIgnoreCase);

    private static string BuildAutoReferenceProvenance(
        Guid speakerId,
        AutoReferenceClipPlan plan,
        ReferenceClipAnalysis analysis)
    {
        string ranges = string.Join(
            ',',
            plan.Ranges.Select(range =>
                $"{range.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-{range.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)}"));
        return string.Join(
            ';',
            "auto-speaker-reference:v2",
            $"speaker:{speakerId:D}",
            $"source-artifact:{plan.SourceArtifact.Id:D}",
            $"source-sha:{plan.SourceArtifact.Sha256}",
            $"mode:{plan.Mode}",
            $"ranges:{ranges}",
            $"fingerprint:{plan.Fingerprint}",
            $"active-speech:{analysis.ActiveSpeechSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
    }

    private static string BuildAutoReferenceFingerprint(
        Guid? transcriptRevisionId,
        ProjectArtifact sourceArtifact,
        IReadOnlyList<AudioClipRange> ranges,
        string mode)
    {
        string raw = string.Join(
            '|',
            transcriptRevisionId?.ToString("D") ?? "none",
            sourceArtifact.Id.ToString("D"),
            sourceArtifact.Sha256,
            mode,
            string.Join(
                ',',
                ranges.Select(range =>
                    $"{range.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-{range.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static bool IsTakeStale(TtsTake take, TranslatedSegment segment)
    {
        bool textChanged = !string.IsNullOrWhiteSpace(take.TranslatedTextHash) &&
                           !string.Equals(
                               take.TranslatedTextHash,
                               TtsTextHash.Compute(segment.SegmentIndex, segment.Text),
                               StringComparison.Ordinal);
        return take.IsStale || textChanged;
    }

    private static double? ComputeTakeDurationSeconds(TtsTake take, ProjectArtifact? artifact)
    {
        return take.DurationSamples is int durationSamples && take.SampleRate is int sampleRate && sampleRate > 0
            ? (double)durationSamples / sampleRate
            : artifact?.DurationSeconds;
    }

    private (TtsDurationSeverity Severity, bool HasDurationWarning, bool HasSpeedLimitWarning, bool CanManualStretch) ComputeDurationAnalysis(
        TranscriptSegment? sourceSegment,
        double? durationSeconds,
        bool isStale,
        TtsTake take,
        ProjectArtifact? artifact)
    {
        double? analysisDurationSeconds = take.PreStretchDurationSeconds ?? durationSeconds;
        DurationAnalysisResult? analysis = sourceSegment is null
            ? null
            : durationAnalysisService.Analyze(sourceSegment, analysisDurationSeconds, timingOptions);
        TtsDurationSeverity severity = take.Status is TtsTakeStatus.Completed && !isStale && analysis is not null
            ? analysis.Severity
            : TtsDurationSeverity.None;
        bool hasSpeedLimitWarning = take.Status is TtsTakeStatus.Completed && !isStale && analysis?.HasSpeedLimitWarning == true;
        bool hasDurationWarning = severity is TtsDurationSeverity.Yellow or TtsDurationSeverity.Red;
        bool canManualStretch = take.Status is TtsTakeStatus.Completed &&
                                !isStale &&
                                take.StretchMode is TtsStretchMode.None &&
                                artifact is not null &&
                                analysis?.IsStretchable == true &&
                                analysis.TempoRatio is double tempoRatio &&
                                Math.Abs(tempoRatio - 1d) >= 0.001d;
        return (severity, hasDurationWarning, hasSpeedLimitWarning, canManualStretch);
    }

    private static string? BuildTtsWarningMessage(bool hasDurationWarning, bool hasSpeedLimitWarning)
    {
        if (!hasDurationWarning && !hasSpeedLimitWarning)
        {
            return null;
        }

        string durationWarning = hasDurationWarning
            ? "TTS duration exceeds the source segment by more than 10%."
            : string.Empty;
        string speedWarning = hasSpeedLimitWarning
            ? "Stretch needs more than 1.5x acceleration; voice quality may degrade."
            : string.Empty;
        return string.Join(' ', new[] { durationWarning, speedWarning }.Where(message => message.Length > 0));
    }

    private static string AppendStretchProvenance(
        string? provenance,
        TtsStretchMode mode,
        TtsStretchEngine engine)
    {
        string stretchProvenance = $"stretch:{mode.ToString().ToLowerInvariant()}:{engine.ToString().ToLowerInvariant()}";
        return string.IsNullOrWhiteSpace(provenance)
            ? stretchProvenance
            : $"{provenance};{stretchProvenance}";
    }

    private async Task<VoiceAssignment> FallBackShortCloneToStockTtsAsync(
        TranscriptProjectState currentState,
        ProjectSpeaker speaker,
        VoiceAssignment currentAssignment,
        string targetLanguage,
        ReferenceClipAnalysis insufficientSpeech,
        HashSet<string>? reservedStockVoiceIds,
        CancellationToken cancellationToken)
    {
        if (TryResolveExistingStockVoiceId(currentAssignment) is string existingVoiceId &&
            StockTtsVoiceMatcher.SupportsKokoro(targetLanguage))
        {
            reservedStockVoiceIds?.Add(existingVoiceId);
            VoiceAssignment keptAssignment = currentAssignment with
            {
                VoiceModelId = StockTtsDefaults.KokoroPrimaryAlias,
                VoiceVariant = existingVoiceId,
                RequiresConsent = false,
                IsFallback = true,
                ReferenceClipArtifactId = null
            };
            await voiceAssignmentRepository.SaveAsync(keptAssignment, cancellationToken).ConfigureAwait(false);
            return keptAssignment;
        }

        return await SubstituteStockVoiceAsync(
                currentState,
                speaker,
                currentAssignment,
                targetLanguage,
                gender: insufficientSpeech.EstimatedGender,
                reservedStockVoiceIds,
                noKokoroMatchError: () =>
                    $"Automatic voice clone reference capture needs at least {ReferenceClipPolicy.MinimumActiveSpeechSeconds:F1} seconds of active speech for {speaker.DisplayName}; detected {insufficientSpeech.ActiveSpeechSeconds:F2} seconds, and no Kokoro voice matched target language '{targetLanguage}'.",
                // Short-clone fallback mints a NEW fallback assignment id rather than transitioning
                // the current assignment in place.
                buildKokoroAssignment: voice => VoiceAssignment.CreateFallback(
                    currentState.ProjectState.Project.Id,
                    speaker.Id,
                    StockTtsDefaults.KokoroPrimaryAlias,
                    voice.VoiceId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private bool IsCloneOnlyStockUnresolvable(VoiceAssignment assignment)
    {
        // The stock lookup resolves VoiceVariant first, then VoiceModelId (see
        // StartTtsStageHandler.ResolveVoiceId). A clone-only assignment carries a clone-model
        // alias in VoiceModelId and no resolvable stock voice, so it must not be handed to the
        // stock voicepack lookup on a non-clone run.
        if (TryResolveExistingStockVoiceId(assignment) is not null)
        {
            return false;
        }

        // Recognize the SAME clone-only alias set (Chatterbox + CosyVoice + Qwen3-base + F5) that
        // StartTtsStageHandler.IsVoiceCloningAlias treats as a clone model, so a persisted
        // Qwen3-base/F5 clone assignment is substituted here instead of falling through to the
        // stock voicepack lookup and throwing "Voicepack '...' is not available.".
        return VoiceCloningDefaults.IsCloneOnlyModelAlias(assignment.VoiceModelId);
    }

    private async Task<VoiceAssignment> SubstituteStockVoiceForCloneOnlyAssignmentAsync(
        TranscriptProjectState currentState,
        ProjectSpeaker speaker,
        VoiceAssignment currentAssignment,
        string targetLanguage,
        HashSet<string>? reservedStockVoiceIds,
        CancellationToken cancellationToken)
    {
        return await SubstituteStockVoiceAsync(
                currentState,
                speaker,
                currentAssignment,
                targetLanguage,
                // Gender matching is intentionally skipped here (gender: null): there is no
                // reference-clip gender analysis on this non-clone substitution path, unlike
                // FallBackShortCloneToStockTtsAsync.
                gender: null,
                reservedStockVoiceIds,
                noKokoroMatchError: () =>
                    $"No stock voice matched target language '{targetLanguage}' to replace the persisted voice-clone model for {speaker.DisplayName}.",
                // Update the existing assignment in place to transition its VoiceModelId and flags
                // without changing its Id, avoiding SQLite primary-key conflicts when SaveAsync
                // performs an INSERT that no longer matches the partial is_fallback=0 index.
                buildKokoroAssignment: voice => currentAssignment with
                {
                    VoiceModelId = StockTtsDefaults.KokoroPrimaryAlias,
                    VoiceVariant = voice.VoiceId,
                    RequiresConsent = false,
                    IsFallback = true,
                    ReferenceClipArtifactId = null
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Shared stock-voice fallback construction and persistence workflow used by both the
    // short-clone fallback (FallBackShortCloneToStockTtsAsync) and the clone-only substitution
    // (SubstituteStockVoiceForCloneOnlyAssignmentAsync) paths. Extracting it keeps the two paths
    // from drifting apart on aliases, flags, or persistence behavior. The paths differ only by the
    // gender passed to PickClosest, the no-match error message, and the Kokoro-branch assignment
    // policy (new-id fallback vs id-preserving in-place transition).
    private async Task<VoiceAssignment> SubstituteStockVoiceAsync(
        TranscriptProjectState currentState,
        ProjectSpeaker speaker,
        VoiceAssignment currentAssignment,
        string targetLanguage,
        string? gender,
        HashSet<string>? reservedStockVoiceIds,
        Func<string> noKokoroMatchError,
        Func<VoiceCatalogEntry, VoiceAssignment> buildKokoroAssignment,
        CancellationToken cancellationToken)
    {
        if (!StockTtsVoiceMatcher.SupportsKokoro(targetLanguage))
        {
            if (!Qwen3TtsDefaults.SupportsLanguage(targetLanguage))
            {
                throw new InvalidOperationException(
                    $"{noKokoroMatchError()} Qwen3 does not speak target language '{targetLanguage}' either.");
            }

            VoiceAssignment stockAssignment = currentAssignment with
            {
                VoiceModelId = StockTtsVoiceMatcher.ResolveFallbackModelAlias(targetLanguage),
                VoiceVariant = null,
                RequiresConsent = false,
                IsFallback = true,
                ReferenceClipArtifactId = null
            };
            await voiceAssignmentRepository.SaveAsync(stockAssignment, cancellationToken).ConfigureAwait(false);
            return stockAssignment;
        }

        IReadOnlyList<VoiceCatalogEntry> catalogVoices = voiceCatalog.GetVoices();
        if (catalogVoices.Count == 0)
        {
            catalogVoices = currentState.AvailableVoices;
        }

        VoiceCatalogEntry voice = StockTtsVoiceMatcher.PickClosest(
                catalogVoices,
                targetLanguage,
                gender,
                reservedStockVoiceIds)
            ?? throw new InvalidOperationException(noKokoroMatchError());

        reservedStockVoiceIds?.Add(voice.VoiceId);
        VoiceAssignment assignment = buildKokoroAssignment(voice);
        await voiceAssignmentRepository.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
        return assignment;
    }

    private static HashSet<string> CollectReservedStockVoiceIds(
        TranscriptProjectState currentState,
        GenerateTtsForAllSpeakersRequest request)
    {
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (VoiceAssignment assignment in currentState.VoiceAssignments)
        {
            if (!string.IsNullOrWhiteSpace(assignment.VoiceVariant))
            {
                reserved.Add(assignment.VoiceVariant);
            }
        }

        if (request.VoiceIdsBySpeakerId is not null)
        {
            foreach (string voiceId in request.VoiceIdsBySpeakerId.Values)
            {
                if (!string.IsNullOrWhiteSpace(voiceId))
                {
                    reserved.Add(voiceId);
                }
            }
        }

        if (request.FallbackVoiceIdsBySpeakerId is not null)
        {
            foreach (string voiceId in request.FallbackVoiceIdsBySpeakerId.Values)
            {
                if (!string.IsNullOrWhiteSpace(voiceId))
                {
                    reserved.Add(voiceId);
                }
            }
        }

        return reserved;
    }

    private string? TryResolveExistingStockVoiceId(VoiceAssignment assignment)
    {
        string candidate = string.IsNullOrWhiteSpace(assignment.VoiceVariant)
            ? assignment.VoiceModelId
            : assignment.VoiceVariant;
        return voiceCatalog.TryGetVoice(candidate, out _) ? candidate : null;
    }

    private static string DescribeStockFallback(VoiceAssignment assignment) =>
        string.IsNullOrWhiteSpace(assignment.VoiceVariant)
            ? assignment.VoiceModelId
            : $"{assignment.VoiceModelId} / {assignment.VoiceVariant}";

    private sealed record PreparedReferenceClip(
        VoiceAssignment Assignment,
        IReadOnlyList<ProjectArtifact> ProjectArtifacts,
        ReferenceClipAnalysis? InsufficientSpeechAnalysis = null);

    private sealed record AutoReferenceCaptureResult(
        ProjectArtifact? Artifact,
        ReferenceClipAnalysis Analysis);

    private sealed record AutoReferenceClipPlan(
        ProjectArtifact SourceArtifact,
        IReadOnlyList<AudioClipRange> Ranges,
        string Mode,
        string Fingerprint);

    private sealed record AutoReferenceClipExtraction(
        AudioClipExtractionResult ExtractionResult,
        ReferenceClipAnalysis Analysis);
}

/// <summary>The voice decisions for one speaker that every synthesized segment of that speaker uses.</summary>
internal sealed record TtsSpeakerPlan(
    VoiceAssignment Assignment,
    string? PreferredModelAlias,
    IReadOnlyList<ProjectArtifact> ProjectArtifacts,
    bool UseReferenceClipForVoiceCloning);
