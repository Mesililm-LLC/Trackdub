using System.Diagnostics.CodeAnalysis;
using Trackdub.Application.Artifacts;
using Trackdub.Application.Logging;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.LipSync;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.LipSync;

/// <summary>Source timing for one translated segment used for source forced-alignment.</summary>
public sealed record SegmentSourceTiming(double StartSeconds, double EndSeconds);

public sealed record LipSyncStageRequest(
    Guid ProjectId,
    MediaAsset MediaAsset,
    IReadOnlyList<TtsTake> TtsTakes,
    IReadOnlyList<ProjectArtifact> ExistingArtifacts,
    bool IsEnabled = true,
    string? PreferredModelAlias = null,
    PhonemeStretchBounds? StretchBounds = null,
    /// <summary>
    /// Maps TranslatedSegmentId → TRANSLATED (target-language) text. Used to align the
    /// TTS take audio, which speaks the translated text.
    /// </summary>
    IReadOnlyDictionary<Guid, string>? SegmentTranscriptMap = null,
    /// <summary>
    /// Maps TranslatedSegmentId → source audio timing for forced-alignment of the original
    /// speech. When present the handler runs source alignment and attempts phoneme stretching.
    /// </summary>
    IReadOnlyDictionary<Guid, SegmentSourceTiming>? SegmentSourceTimingMap = null,
    /// <summary>
    /// Maps TranslatedSegmentId → ORIGINAL (source-language) transcript text. Used to align
    /// the source audio clip, which speaks the original language. Never reuse the translated
    /// map here: aligning source audio against translated text yields bogus phoneme timings.
    /// </summary>
    IReadOnlyDictionary<Guid, string>? SourceSegmentTranscriptMap = null,
    /// <summary>
    /// Absolute path to the best available source audio file (NormalizedAudio, enhanced, or vocals).
    /// Required when SegmentSourceTimingMap is provided.
    /// </summary>
    string? SourceAudioPath = null,
    /// <summary>
    /// BCP-47 source language for eSpeak phonemization of source-audio alignment.
    /// </summary>
    string? SourceLanguageCode = null,
    /// <summary>
    /// BCP-47 target language for eSpeak phonemization of TTS-take alignment.
    /// </summary>
    string? TargetLanguageCode = null);

public sealed record LipSyncStageResult(
    StageRunRecord StageRun,
    IReadOnlyList<LipSyncSegment> Segments);

public sealed class LipSyncStageHandler(
    IForcedAligner forcedAligner,
    IPhonemeTimingPlanner phonemeTimingPlanner,
    IPhonemeStretchService phonemeStretchService,
    IArtifactStore artifactStore,
    IProjectStageRunStore stageRunStore,
    ILipSyncSegmentRepository segmentRepository,
    PipelineDegradationWriter? degradationWriter = null,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IApplicationLogger? logger = null,
    IFileFingerprintService? fileFingerprintService = null,
    IMediaAssetRepository? mediaAssetRepository = null,
    IAudioClipExtractor? audioClipExtractor = null)
{
    private readonly ILipSyncSegmentRepository _segmentRepository = segmentRepository ?? throw new ArgumentNullException(nameof(segmentRepository));
    private readonly IPhonemeTimingPlanner _phonemeTimingPlanner = phonemeTimingPlanner;
    private readonly IPhonemeStretchService _phonemeStretchService = phonemeStretchService;
    private readonly IFileFingerprintService? _fileFingerprintService = fileFingerprintService;
    private readonly IMediaAssetRepository? _mediaAssetRepository = mediaAssetRepository;
    private readonly IAudioClipExtractor? _audioClipExtractor = audioClipExtractor;

    private static readonly PhonemeStretchBounds DefaultStretchBounds =
        new(MinRatio: 0.5, MaxRatio: 2.0, PreferredMaxVowelRatio: 1.5);

    public async Task<LipSyncStageResult> HandleAsync(
        LipSyncStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        StageRunRecord stageRun = await StageRunHelper
            .StartAsync(stageRunStore, request.ProjectId, StageNames.LipSync, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (!request.IsEnabled)
            {
                stageRun = await SkipDisabledAsync(stageRun, request).ConfigureAwait(false);
                return new LipSyncStageResult(stageRun, []);
            }

            var bounds = request.StretchBounds ?? DefaultStretchBounds;
            List<LipSyncSegment> segments = await ProcessAllTakesAsync(request, stageRun.Id, bounds, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (segments.Count == 0)
            {
                stageRun = await SkipEmptyAsync(stageRun).ConfigureAwait(false);
                return new LipSyncStageResult(stageRun, []);
            }

            stageRun = await ResolveOutcomeAsync(stageRun, segments).ConfigureAwait(false);
            return new LipSyncStageResult(stageRun, segments);
        }
        catch (OperationCanceledException)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                    "LipSync canceled.", CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await FailWithDegradationAsync(stageRun, request, ex).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<StageRunRecord> SkipDisabledAsync(StageRunRecord stageRun, LipSyncStageRequest request)
    {
        const string skipReason = "Lip-sync stage is disabled for this run.";
        stageRun = await StageRunHelper
            .SkipAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                skipReason, CancellationToken.None, runtimePlanningPreferences, logger)
            .ConfigureAwait(false);

        await WriteDegradationAsync(
            request, stageRun.Id,
            "LipSyncDisabled", skipReason, null, "original-tts-take", null)
            .ConfigureAwait(false);
        return stageRun;
    }

    private Task<StageRunRecord> SkipEmptyAsync(StageRunRecord stageRun)
    {
        return StageRunHelper.SkipAsync(
            stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
            "No TTS takes provided; lip-sync prerequisite not met.", CancellationToken.None,
            runtimePlanningPreferences, logger);
    }

    private async Task<List<LipSyncSegment>> ProcessAllTakesAsync(
        LipSyncStageRequest request,
        Guid stageRunId,
        PhonemeStretchBounds bounds,
        CancellationToken cancellationToken)
    {
        var segments = new List<LipSyncSegment>(request.TtsTakes.Count);
        foreach (var take in request.TtsTakes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var segment = await ProcessTakeAsync(take, bounds, stageRunId, request, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // Artifact is already published; record its row even if cancellation just arrived.
            await _segmentRepository.SaveAllAsync(request.ProjectId, stageRunId, [segment], CancellationToken.None)
                .ConfigureAwait(false);
            segments.Add(segment);
        }

        return segments;
    }

    private Task<StageRunRecord> ResolveOutcomeAsync(StageRunRecord stageRun, List<LipSyncSegment> segments)
    {
        bool anyAligned = segments.Any(s => s.Status == LipSyncSegmentStatus.Aligned);
        bool anyPartial = segments.Any(s => s.Status == LipSyncSegmentStatus.Partial);
        bool anyFailed = segments.Any(s => s.Status == LipSyncSegmentStatus.Failed);
        bool allSkipped = segments.All(s =>
            s.Status is not (LipSyncSegmentStatus.Aligned or LipSyncSegmentStatus.Partial or LipSyncSegmentStatus.Failed));

        var reporter = forcedAligner as IStageRuntimeExecutionReporter;
        if (allSkipped)
        {
            return StageRunHelper.SkipAsync(stageRunStore, stageRun, reporter,
                "All segments skipped during lip-sync alignment.", CancellationToken.None,
                runtimePlanningPreferences, logger);
        }

        if (anyAligned && anyFailed)
        {
            int failed = segments.Count(s => s.Status == LipSyncSegmentStatus.Failed);
            return StageRunHelper.PartiallyCompleteAsync(stageRunStore, stageRun, reporter,
                $"Some segments aligned, but {failed} failed.",
                CancellationToken.None, runtimePlanningPreferences, logger);
        }

        if (anyAligned && !anyPartial && !anyFailed)
        {
            return StageRunHelper.CompleteAsync(stageRunStore, stageRun, reporter,
                CancellationToken.None, runtimePlanningPreferences);
        }

        if (anyFailed && !anyAligned && !anyPartial)
        {
            return StageRunHelper.FailAsync(stageRunStore, stageRun, reporter,
                $"All {segments.Count} segments failed lip-sync.", CancellationToken.None,
                runtimePlanningPreferences, logger);
        }

        int failedCount = segments.Count(s => s.Status == LipSyncSegmentStatus.Failed);
        int partialCount = segments.Count(s => s.Status == LipSyncSegmentStatus.Partial);
        int alignedCount = segments.Count(s => s.Status == LipSyncSegmentStatus.Aligned);
        return StageRunHelper.PartiallyCompleteAsync(stageRunStore, stageRun, reporter,
            $"{partialCount} partial, {failedCount} failed, {alignedCount} aligned.",
            CancellationToken.None, runtimePlanningPreferences, logger);
    }

    private async Task FailWithDegradationAsync(
        StageRunRecord stageRun,
        LipSyncStageRequest request,
        Exception ex)
    {
        await StageRunHelper
            .FailAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                ex.Message, CancellationToken.None, runtimePlanningPreferences, logger)
            .ConfigureAwait(false);

        await WriteDegradationAsync(
            request, stageRun.Id,
            "LipSyncUnhandledFailure", "Unhandled exception in lip-sync stage.",
            ex.Message, "original-tts-take", "Check aligner configuration and audio paths.")
            .ConfigureAwait(false);
    }

    private Task WriteDegradationAsync(
        LipSyncStageRequest request,
        Guid stageRunId,
        string code,
        string message,
        string? detail,
        string fallback,
        string? recommendedAction)
    {
        if (degradationWriter is null)
        {
            return Task.CompletedTask;
        }

        return degradationWriter.WriteAsync(
            new PipelineDegradationRecord(
                Stage: StageNames.LipSync,
                Code: code,
                Message: message,
                Detail: detail,
                SelectedFallback: fallback,
                RecommendedAction: recommendedAction,
                OccurredAtUtc: DateTimeOffset.UtcNow,
                StageRunId: stageRunId),
            request.ProjectId, request.MediaAsset.Id, CancellationToken.None);
    }

    private async Task<LipSyncSegment> ProcessTakeAsync(
        TtsTake take,
        PhonemeStretchBounds bounds,
        Guid stageRunId,
        LipSyncStageRequest request,
        CancellationToken cancellationToken)
    {
        if (TryCreateMissingReferenceSegment(take, out LipSyncSegment? missingRef))
        {
            return missingRef!;
        }

        ProjectArtifact? ttsTakeArtifact = FindTtsArtifact(take, request);
        if (ttsTakeArtifact is null)
        {
            return CreateMissingArtifactSegment(take, request);
        }

        string ttsTakeAudioPath = artifactStore.GetPath(ttsTakeArtifact.RelativePath);
        ForcedAlignmentRequest alignmentRequest = BuildTtsAlignmentRequest(take, request, ttsTakeAudioPath);

        ForcedAlignmentResult alignmentResult = await forcedAligner
            .AlignAsync(alignmentRequest, cancellationToken)
            .ConfigureAwait(false);

        if (alignmentResult.Status == ForcedAlignmentStatus.Skipped)
        {
            return await CreateSkippedAlignmentSegmentAsync(take, request, stageRunId, alignmentResult)
                .ConfigureAwait(false);
        }

        if (TryCreateTtsGuardSegment(take, alignmentRequest, alignmentResult, out LipSyncSegment? guard))
        {
            return guard!;
        }

        return await ProcessSourceAndStretchAsync(
                take, request, stageRunId, ttsTakeAudioPath, alignmentResult, bounds, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryCreateMissingReferenceSegment(TtsTake take, out LipSyncSegment? segment)
    {
        if (take.ArtifactId is not null && take.TranslatedSegmentId is not null)
        {
            segment = null;
            return false;
        }

        segment = BuildSegment(
            take.TranslatedSegmentId ?? take.Id,
            LipSyncSegmentStatus.SkippedNoPhonemes,
            ttsDuration: TimeSpan.Zero,
            skipReason: "TTS take has no artifact or segment reference.");
        return true;
    }

    private static ProjectArtifact? FindTtsArtifact(TtsTake take, LipSyncStageRequest request)
    {
        return request.ExistingArtifacts.FirstOrDefault(a => a.Id == take.ArtifactId);
    }

    private static LipSyncSegment CreateMissingArtifactSegment(TtsTake take, LipSyncStageRequest request)
    {
        return BuildSegment(
            take.TranslatedSegmentId!.Value,
            LipSyncSegmentStatus.SkippedNoPhonemes,
            ttsDuration: ToDuration(take.PreStretchDurationSeconds),
            skipReason: "TTS artifact not found in project artifacts list.");
    }

    private static ForcedAlignmentRequest BuildTtsAlignmentRequest(
        TtsTake take,
        LipSyncStageRequest request,
        string ttsTakeAudioPath)
    {
        string transcript = request.SegmentTranscriptMap is not null
            && request.SegmentTranscriptMap.TryGetValue(take.TranslatedSegmentId!.Value, out string? text)
            ? text
            : string.Empty;
        return new ForcedAlignmentRequest(
            AudioPath: ttsTakeAudioPath,
            NormalizedTranscript: transcript,
            LanguageCode: request.TargetLanguageCode,
            SegmentId: take.TranslatedSegmentId!.Value.ToString(),
            Options: new ForcedAlignmentOptions(
                RequirePhonemeTimings: true,
                PreferredModelAlias: request.PreferredModelAlias));
    }

    private async Task<LipSyncSegment> CreateSkippedAlignmentSegmentAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid stageRunId,
        ForcedAlignmentResult alignmentResult)
    {
        await WriteDegradationAsync(
            request, stageRunId,
            "LipSyncAlignmentSkipped", alignmentResult.SkipReason ?? "Alignment skipped.",
            $"SegmentId={take.TranslatedSegmentId}", "original-tts-take", null)
            .ConfigureAwait(false);

        return BuildSegment(
            take.TranslatedSegmentId!.Value,
            LipSyncSegmentStatus.SkippedLowConfidence,
            skipReason: alignmentResult.SkipReason,
            providerId: alignmentResult.ProviderId,
            modelId: alignmentResult.ModelId);
    }

    private static bool TryCreateTtsGuardSegment(
        TtsTake take,
        ForcedAlignmentRequest alignmentRequest,
        ForcedAlignmentResult alignmentResult,
        out LipSyncSegment? segment)
    {
        Guid segmentId = take.TranslatedSegmentId!.Value;

        if (alignmentResult.Status == ForcedAlignmentStatus.Failed)
        {
            segment = BuildSegment(segmentId, LipSyncSegmentStatus.Failed,
                failureReason: "Aligner returned Failed status.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
            return true;
        }

        if (alignmentResult.Phonemes.Count == 0)
        {
            segment = BuildSegment(segmentId, LipSyncSegmentStatus.SkippedNoPhonemes,
                skipReason: "Aligner returned zero phonemes.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
            return true;
        }

        if (alignmentResult.Confidence.Overall < alignmentRequest.Options.MinOverallConfidence)
        {
            segment = BuildSegment(segmentId, LipSyncSegmentStatus.SkippedLowConfidence,
                planConfidence: alignmentResult.Confidence.Overall,
                skipReason: $"Overall confidence {alignmentResult.Confidence.Overall:F2} below threshold {alignmentRequest.Options.MinOverallConfidence}.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
            return true;
        }

        segment = null;
        return false;
    }

    private async Task<LipSyncSegment> ProcessSourceAndStretchAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid stageRunId,
        string ttsTakeAudioPath,
        ForcedAlignmentResult alignmentResult,
        PhonemeStretchBounds bounds,
        CancellationToken cancellationToken)
    {
        Guid segmentId = take.TranslatedSegmentId!.Value;

        if (!TryGetSourceTiming(request, segmentId, out SegmentSourceTiming? sourceTiming))
        {
            return BuildSegment(segmentId, LipSyncSegmentStatus.Partial,
                ttsAlignmentId: alignmentResult.SegmentId,
                ttsDuration: ToDuration(take.PreStretchDurationSeconds),
                planConfidence: alignmentResult.Confidence.Overall,
                skipReason: "Source audio or timing not available; phoneme stretch skipped.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
        }

        if (!TryGetSourceTranscript(request, segmentId, out string sourceTranscript))
        {
            return BuildSegment(segmentId, LipSyncSegmentStatus.Partial,
                ttsAlignmentId: alignmentResult.SegmentId,
                sourceDuration: SourceDuration(sourceTiming),
                ttsDuration: ToDuration(take.PreStretchDurationSeconds),
                planConfidence: alignmentResult.Confidence.Overall,
                skipReason: "Source-language transcript not available; phoneme stretch skipped.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
        }

        SourceClipExtract extract = await TryExtractSourceClipAsync(
                take, request, segmentId, sourceTiming, alignmentResult, cancellationToken)
            .ConfigureAwait(false);
        if (!extract.Succeeded)
        {
            return extract.FailureSegment!;
        }

        ForcedAlignmentResult sourceAlignmentResult = await AlignSourceClipAsync(
                take, request, segmentId, sourceTranscript, extract.ClipPath!, cancellationToken)
            .ConfigureAwait(false);

        if (IsSourceAlignmentUnusable(sourceAlignmentResult))
        {
            return BuildSegment(segmentId, LipSyncSegmentStatus.Partial,
                sourceAlignmentId: sourceAlignmentResult.SegmentId,
                ttsAlignmentId: alignmentResult.SegmentId,
                sourceDuration: SourceDuration(sourceTiming),
                ttsDuration: ToDuration(take.PreStretchDurationSeconds),
                planConfidence: alignmentResult.Confidence.Overall,
                skipReason: "Source alignment produced no phonemes; stretch skipped.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
        }

        return await PlanStretchAndPersistAsync(
                take, request, stageRunId, ttsTakeAudioPath, sourceTiming,
                alignmentResult, sourceAlignmentResult, bounds, cancellationToken)
            .ConfigureAwait(false);
    }

    private bool TryGetSourceTiming(
        LipSyncStageRequest request,
        Guid segmentId,
        [NotNullWhen(true)] out SegmentSourceTiming? sourceTiming)
    {
        sourceTiming = null;
        return _audioClipExtractor is not null
            && !string.IsNullOrEmpty(request.SourceAudioPath)
            && request.SegmentSourceTimingMap is not null
            && request.SegmentSourceTimingMap.TryGetValue(segmentId, out sourceTiming);
    }

    private static bool TryGetSourceTranscript(
        LipSyncStageRequest request,
        Guid segmentId,
        out string sourceTranscript)
    {
        sourceTranscript = request.SourceSegmentTranscriptMap is not null
            && request.SourceSegmentTranscriptMap.TryGetValue(segmentId, out string? sourceText)
            ? sourceText
            : string.Empty;
        return !string.IsNullOrWhiteSpace(sourceTranscript);
    }

    private sealed record SourceClipExtract(bool Succeeded, string? ClipPath, LipSyncSegment? FailureSegment);

    private async Task<SourceClipExtract> TryExtractSourceClipAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid segmentId,
        SegmentSourceTiming sourceTiming,
        ForcedAlignmentResult alignmentResult,
        CancellationToken cancellationToken)
    {
        string tempSourceSegmentPath = Path.Join(Path.GetTempPath(), $"trackdub-src-seg-{segmentId:N}.wav");
        bool extracted = false;
        try
        {
            await _audioClipExtractor!
                .ExtractAsync(
                    request.SourceAudioPath!,
                    sourceTiming.StartSeconds,
                    sourceTiming.EndSeconds,
                    tempSourceSegmentPath,
                    cancellationToken)
                .ConfigureAwait(false);
            extracted = true;
            return new SourceClipExtract(true, tempSourceSegmentPath, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new SourceClipExtract(false, null, BuildSegment(segmentId, LipSyncSegmentStatus.Partial,
                ttsAlignmentId: alignmentResult.SegmentId,
                planConfidence: alignmentResult.Confidence.Overall,
                skipReason: $"Source audio segment extraction failed: {ex.Message}",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId));
        }
        finally
        {
            if (!extracted)
            {
                try { File.Delete(tempSourceSegmentPath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private async Task<ForcedAlignmentResult> AlignSourceClipAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid segmentId,
        string sourceTranscript,
        string tempSourceSegmentPath,
        CancellationToken cancellationToken)
    {
        try
        {
            var sourceAlignmentRequest = new ForcedAlignmentRequest(
                AudioPath: tempSourceSegmentPath,
                NormalizedTranscript: sourceTranscript,
                LanguageCode: request.SourceLanguageCode,
                SegmentId: $"src-{segmentId}",
                Options: new ForcedAlignmentOptions(
                    AllowPartial: true,
                    RequirePhonemeTimings: true,
                    PreferredModelAlias: request.PreferredModelAlias));

            return await forcedAligner
                .AlignAsync(sourceAlignmentRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempSourceSegmentPath); } catch { /* best-effort cleanup */ }
        }
    }

    private static bool IsSourceAlignmentUnusable(ForcedAlignmentResult sourceAlignmentResult)
    {
        return sourceAlignmentResult.Status is ForcedAlignmentStatus.Failed
            || sourceAlignmentResult.Phonemes.Count == 0;
    }

    private async Task<LipSyncSegment> PlanStretchAndPersistAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid stageRunId,
        string ttsTakeAudioPath,
        SegmentSourceTiming sourceTiming,
        ForcedAlignmentResult alignmentResult,
        ForcedAlignmentResult sourceAlignmentResult,
        PhonemeStretchBounds bounds,
        CancellationToken cancellationToken)
    {
        Guid segmentId = take.TranslatedSegmentId!.Value;

        IReadOnlyList<PhonemeStretchPlan> stretchPlan =
            _phonemeTimingPlanner.PlanStretches(
                sourceAlignmentResult.Phonemes,
                alignmentResult.Phonemes,
                bounds);

        if (IsUnsafeStretch(stretchPlan))
        {
            return await CreateUnsafeStretchSegmentAsync(take, request, stageRunId, sourceTiming, alignmentResult, sourceAlignmentResult,
                    stretchPlan.All(static p => p.PlanningReason == "unmatched_viseme"))
                .ConfigureAwait(false);
        }

        StretchOutcome stretch = await TryStretchTtsAsync(
                take, ttsTakeAudioPath, segmentId, sourceTiming, alignmentResult, sourceAlignmentResult,
                stretchPlan, cancellationToken)
            .ConfigureAwait(false);
        if (!stretch.Succeeded)
        {
            return stretch.FailureSegment!;
        }

        return await PersistStretchedOutputAsync(
                take, request, stageRunId, stretch.Result!,
                sourceTiming, alignmentResult, sourceAlignmentResult,
                stretch.OutputPath!, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsUnsafeStretch(IReadOnlyList<PhonemeStretchPlan> stretchPlan)
    {
        return stretchPlan.Count > 0 && stretchPlan.All(static p => !p.WithinBounds);
    }

    private async Task<LipSyncSegment> CreateUnsafeStretchSegmentAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid stageRunId,
        SegmentSourceTiming sourceTiming,
        ForcedAlignmentResult alignmentResult,
        ForcedAlignmentResult sourceAlignmentResult,
        bool inventoryMismatch)
    {
        Guid segmentId = take.TranslatedSegmentId!.Value;
        await WriteDegradationAsync(
            request, stageRunId,
            inventoryMismatch ? "LipSyncInventoryMismatch" : "LipSyncUnsafeStretch",
            inventoryMismatch ? "No cross-inventory viseme correspondence; original TTS take preserved."
                : "All phoneme stretch ratios are out of safe bounds; original TTS take preserved.",
            $"SegmentId={take.TranslatedSegmentId}", "original-tts-take", null)
            .ConfigureAwait(false);

        return BuildSegment(segmentId, inventoryMismatch
                ? LipSyncSegmentStatus.SkippedInventoryMismatch
                : LipSyncSegmentStatus.SkippedUnsafeStretchRatio,
            sourceAlignmentId: sourceAlignmentResult.SegmentId,
            ttsAlignmentId: alignmentResult.SegmentId,
            sourceDuration: SourceDuration(sourceTiming),
            ttsDuration: ToDuration(take.PreStretchDurationSeconds),
            planConfidence: alignmentResult.Confidence.Overall,
            skipReason: inventoryMismatch
                ? "No source and dubbed phonemes share a supported viseme class."
                : "All phoneme stretch ratios are outside safe bounds.",
            providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
    }

    private sealed record StretchOutcome(bool Succeeded, string? OutputPath, PhonemeStretchResult? Result, LipSyncSegment? FailureSegment);

    private async Task<StretchOutcome> TryStretchTtsAsync(
        TtsTake take,
        string ttsTakeAudioPath,
        Guid segmentId,
        SegmentSourceTiming sourceTiming,
        ForcedAlignmentResult alignmentResult,
        ForcedAlignmentResult sourceAlignmentResult,
        IReadOnlyList<PhonemeStretchPlan> stretchPlan,
        CancellationToken cancellationToken)
    {
        string stretchedOutputPath = Path.Join(Path.GetTempPath(), $"trackdub-lip-stretched-{segmentId:N}.wav");
        PhonemeStretchResult? stretchResult;
        try
        {
            stretchResult = await _phonemeStretchService
                .StretchAsync(ttsTakeAudioPath, stretchedOutputPath, stretchPlan, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = BuildSegment(segmentId, LipSyncSegmentStatus.Failed,
                sourceAlignmentId: sourceAlignmentResult.SegmentId,
                ttsAlignmentId: alignmentResult.SegmentId,
                sourceDuration: SourceDuration(sourceTiming),
                planConfidence: alignmentResult.Confidence.Overall,
                failureReason: $"Phoneme stretch failed: {ex.Message}",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
            return new StretchOutcome(false, null, null, failed);
        }

        if (stretchResult is null)
        {
            try { File.Delete(stretchedOutputPath); } catch { /* best-effort */ }
            var skipped = BuildSegment(segmentId, LipSyncSegmentStatus.SkippedNoApplicableStretch,
                sourceAlignmentId: sourceAlignmentResult.SegmentId,
                ttsAlignmentId: alignmentResult.SegmentId,
                sourceDuration: SourceDuration(sourceTiming),
                ttsDuration: ToDuration(take.PreStretchDurationSeconds),
                planConfidence: alignmentResult.Confidence.Overall,
                skipReason: "No phoneme region could be safely stretched.",
                providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
            return new StretchOutcome(false, null, null, skipped);
        }

        return new StretchOutcome(true, stretchedOutputPath, stretchResult, null);
    }

    private async Task<LipSyncSegment> PersistStretchedOutputAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid stageRunId,
        PhonemeStretchResult stretchResult,
        SegmentSourceTiming sourceTiming,
        ForcedAlignmentResult alignmentResult,
        ForcedAlignmentResult sourceAlignmentResult,
        string stretchedOutputPath,
        CancellationToken cancellationToken)
    {
        Guid segmentId = take.TranslatedSegmentId!.Value;
        bool anyUnchanged = stretchResult.Regions.Any(static p =>
            p.Status != PhonemeStretchRegionStatus.Stretched);
        string relativeOutputPath = ProjectArtifactPaths.GetLipSyncTakeRelativePath(segmentId, stageRunId);

        string absOutputPath = await CommitStretchedFileAsync(relativeOutputPath, stretchedOutputPath, cancellationToken)
            .ConfigureAwait(false);

        await RegisterLipSyncArtifactAsync(take, request, stageRunId, relativeOutputPath, absOutputPath, stretchResult.Duration, cancellationToken)
            .ConfigureAwait(false);

        return BuildSegment(segmentId,
            anyUnchanged ? LipSyncSegmentStatus.Partial : LipSyncSegmentStatus.Aligned,
            sourceAlignmentId: sourceAlignmentResult.SegmentId,
            ttsAlignmentId: alignmentResult.SegmentId,
            sourceDuration: SourceDuration(sourceTiming),
            ttsDuration: ToDuration(take.PreStretchDurationSeconds),
            alignedDuration: stretchResult.Duration,
            planConfidence: alignmentResult.Confidence.Overall,
            skipReason: anyUnchanged ? "Some phoneme regions were not stretched." : null,
            providerId: alignmentResult.ProviderId, modelId: alignmentResult.ModelId);
    }

    private async Task<string> CommitStretchedFileAsync(
        string relativeOutputPath,
        string stretchedOutputPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(relativeOutputPath));
            File.Copy(stretchedOutputPath, tx.TemporaryPath, overwrite: true);
            await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);
            return artifactStore.GetPath(relativeOutputPath);
        }
        finally
        {
            try { File.Delete(stretchedOutputPath); } catch { /* best-effort */ }
        }
    }

    private async Task RegisterLipSyncArtifactAsync(
        TtsTake take,
        LipSyncStageRequest request,
        Guid stageRunId,
        string relativeOutputPath,
        string absOutputPath,
        TimeSpan alignedDuration,
        CancellationToken cancellationToken)
    {
        Guid segmentId = take.TranslatedSegmentId!.Value;
        if (_mediaAssetRepository is null)
        {
            await WriteDegradationAsync(
                request, stageRunId,
                "LipSyncArtifactNotRegistered",
                "Lip-sync alignment succeeded but the LipSyncTake artifact metadata could not be registered in the project store; mix planner will not discover the aligned output.",
                $"SegmentId={take.TranslatedSegmentId}, ArtifactPath={relativeOutputPath}",
                "original-tts-take",
                "Ensure IMediaAssetRepository is registered in the DI container.")
                .ConfigureAwait(false);

            logger?.LogWarning(
                "Lip-sync alignment succeeded but LipSyncTake artifact metadata was not registered; IMediaAssetRepository unavailable. SegmentId={SegmentId}",
                take.TranslatedSegmentId);
            return;
        }

        string sha256 = "unknown";
        long sizeBytes = 0L;
        if (_fileFingerprintService is not null)
        {
            FileFingerprint fp = await _fileFingerprintService
                .ComputeAsync(absOutputPath, cancellationToken)
                .ConfigureAwait(false);
            sha256 = fp.Sha256;
            sizeBytes = fp.SizeBytes;
        }

        var lipSyncArtifact = new ProjectArtifact(
            Id: Guid.NewGuid(),
            ProjectId: request.ProjectId,
            MediaAssetId: request.MediaAsset.Id,
            Kind: ArtifactKind.LipSyncTake,
            RelativePath: relativeOutputPath,
            Sha256: sha256,
            SizeBytes: sizeBytes,
            DurationSeconds: alignedDuration.TotalSeconds,
            SampleRate: null,
            ChannelCount: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            StageRunId: stageRunId,
            Provenance: $"lipsync:take:{take.Id:N}");

        await _mediaAssetRepository
            .SaveArtifactAsync(lipSyncArtifact, cancellationToken)
            .ConfigureAwait(false);
    }

    private static TimeSpan ToDuration(double? seconds)
    {
        return seconds.HasValue ? TimeSpan.FromSeconds(seconds.Value) : TimeSpan.Zero;
    }

    private static TimeSpan SourceDuration(SegmentSourceTiming timing)
    {
        return TimeSpan.FromSeconds(timing.EndSeconds - timing.StartSeconds);
    }

    private static LipSyncSegment BuildSegment(
        Guid segmentId,
        LipSyncSegmentStatus status,
        string? sourceAlignmentId = null,
        string? ttsAlignmentId = null,
        TimeSpan? sourceDuration = null,
        TimeSpan? ttsDuration = null,
        TimeSpan? alignedDuration = null,
        double? planConfidence = null,
        string? skipReason = null,
        string? failureReason = null,
        string? providerId = null,
        string? modelId = null)
    {
        return new LipSyncSegment(
            SegmentId: segmentId,
            Status: status,
            SourceAlignmentId: sourceAlignmentId,
            TtsAlignmentId: ttsAlignmentId,
            SourceDuration: sourceDuration ?? TimeSpan.Zero,
            TtsDuration: ttsDuration ?? TimeSpan.Zero,
            AlignedTtsDuration: alignedDuration,
            PlanConfidence: planConfidence,
            SkipReason: skipReason,
            FailureReason: failureReason,
            ProviderId: providerId,
            ModelId: modelId,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }
}
