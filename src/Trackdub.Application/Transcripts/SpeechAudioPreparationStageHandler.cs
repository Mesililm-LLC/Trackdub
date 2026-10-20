using Trackdub.Application.Artifacts;
using Trackdub.Application.Logging;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Projects;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed class SpeechAudioPreparationStageHandler(
    IAudioQualityAnalyzer audioQualityAnalyzer,
    ISpeechAudioPreparationPlanner preparationPlanner,
    ISpeechAudioProcessingService processingService,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    IProjectStageRunStore stageRunStore,
    IApplicationLogger? logger = null,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    PipelineDegradationWriter? degradationWriter = null)
{
    private readonly IAudioQualityAnalyzer audioQualityAnalyzer = audioQualityAnalyzer ?? throw new ArgumentNullException(nameof(audioQualityAnalyzer));
    private readonly ISpeechAudioPreparationPlanner preparationPlanner = preparationPlanner ?? throw new ArgumentNullException(nameof(preparationPlanner));
    private readonly ISpeechAudioProcessingService processingService = processingService ?? throw new ArgumentNullException(nameof(processingService));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public async Task<TranscriptAudioRoutingPlan> HandleAsync(
        SpeechAudioPreparationStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        StageRunRecord stageRun = await StageRunHelper
            .StartAsync(stageRunStore, request.ProjectId, StageNames.AudioPreparation, cancellationToken)
            .ConfigureAwait(false);

        // Artifacts are registered while the run is Running, mirroring the stem and lip
        // layers: the completed producer row then accepts the whole persisted set, and an
        // interrupted registration removes its rows so a canceled or failed producer can
        // never be selected by a later consumer.
        var registeredArtifactIds = new List<Guid>();
        try
        {
            AudioQualityAnalysisResult fullMixAnalysis = await AnalyzeAsync(
                request.NormalizedAudioArtifact,
                SpeechAudioSourceKind.FullMix,
                cancellationToken).ConfigureAwait(false);
            SpeechAudioPreparationPlan preparationPlan = preparationPlanner.Plan(
                new SpeechAudioPreparationPlanningRequest(fullMixAnalysis));

            ProjectArtifact selectedSourceArtifact = request.NormalizedAudioArtifact;
            Guid analysisArtifactId = Guid.NewGuid();
            var processedArtifacts = new Dictionary<Guid, ProjectArtifact>();

            SpeechAudioStageDecision vadDecision = await ProcessStageIfNeededAsync(
                request,
                stageRun,
                selectedSourceArtifact,
                preparationPlan.VadDecision,
                preparationPlan.FullMixAnalysis,
                analysisArtifactId,
                processedArtifacts,
                registeredArtifactIds,
                cancellationToken).ConfigureAwait(false);
            SpeechAudioStageDecision asrDecision = await ProcessStageIfNeededAsync(
                request,
                stageRun,
                selectedSourceArtifact,
                preparationPlan.AsrDecision,
                preparationPlan.FullMixAnalysis,
                analysisArtifactId,
                processedArtifacts,
                registeredArtifactIds,
                cancellationToken).ConfigureAwait(false);
            SpeechAudioStageDecision diarizationDecision = await ProcessStageIfNeededAsync(
                request,
                stageRun,
                selectedSourceArtifact,
                preparationPlan.DiarizationDecision,
                preparationPlan.FullMixAnalysis,
                analysisArtifactId,
                processedArtifacts,
                registeredArtifactIds,
                cancellationToken).ConfigureAwait(false);

            var audit = SpeechAudioPreparationAudit.Create(
                request,
                preparationPlan,
                vadDecision,
                asrDecision,
                diarizationDecision);

            string analysisRelativePath = ProjectArtifactPaths.GetAudioQualityAnalysisRelativePath(stageRun.Id);
            await artifactStore.WriteJsonAsync(analysisRelativePath, audit, cancellationToken).ConfigureAwait(false);
            FileFingerprint analysisFingerprint = await fileFingerprintService
                .ComputeAsync(artifactStore.GetPath(analysisRelativePath), cancellationToken)
                .ConfigureAwait(false);

            ProjectArtifact analysisArtifact = CreateAnalysisArtifact(
                request,
                stageRun,
                preparationPlan,
                analysisArtifactId,
                analysisRelativePath,
                analysisFingerprint);

            await mediaAssetRepository.SaveArtifactAsync(analysisArtifact, cancellationToken).ConfigureAwait(false);
            registeredArtifactIds.Add(analysisArtifact.Id);

            var routingPlan = new TranscriptAudioRoutingPlan(
                ResolveArtifact(selectedSourceArtifact, vadDecision, processedArtifacts),
                ResolveArtifact(selectedSourceArtifact, asrDecision, processedArtifacts),
                ResolveArtifact(selectedSourceArtifact, diarizationDecision, processedArtifacts),
                preparationPlan.SelectedSourceKind,
                analysisArtifact,
                vadDecision,
                asrDecision,
                diarizationDecision);

            cancellationToken.ThrowIfCancellationRequested();
            await StageRunHelper
                .CompleteAsync(stageRunStore, stageRun, null, cancellationToken, runtimePlanningPreferences)
                .ConfigureAwait(false);

            return routingPlan;
        }
        catch (OperationCanceledException)
        {
            await CleanupRegisteredArtifactsAsync(registeredArtifactIds).ConfigureAwait(false);
            await StageRunHelper
                .CancelAsync(stageRunStore, stageRun, null, "Speech audio preparation canceled.", CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await CleanupRegisteredArtifactsAsync(registeredArtifactIds).ConfigureAwait(false);
            await StageRunHelper
                .FailAsync(stageRunStore, stageRun, null, ex.Message, cancellationToken, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task CleanupRegisteredArtifactsAsync(IEnumerable<Guid> registeredArtifactIds)
    {
        foreach (Guid artifactId in registeredArtifactIds)
        {
            try
            {
                await mediaAssetRepository.DeleteArtifactAsync(artifactId, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original cancellation/failure; cleanup errors should not replace it.
            }
        }
    }

    private async Task<AudioQualityAnalysisResult> AnalyzeAsync(
        ProjectArtifact artifact,
        SpeechAudioSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        AudioQualityAnalysisResult analysis = await audioQualityAnalyzer
            .AnalyzeAsync(
                new AudioQualityAnalysisRequest(
                    artifactStore.GetPath(artifact.RelativePath),
                    sourceKind,
                    AudioQualityAnalysisThresholds.ForSource(sourceKind)),
                cancellationToken)
            .ConfigureAwait(false);

        logger?.LogDebug(
            $"Audio quality analysis ({sourceKind}): peak={analysis.Metrics.PeakDbfs:F1}dBFS " +
            $"activeRms={analysis.Metrics.ActiveRmsDbfs:F1}dBFS snr={FormatNullable(analysis.Metrics.SnrDb)} " +
            $"defects={string.Join(',', analysis.TriggeredDefects)}");
        return analysis with
        {
            AudioPath = artifact.RelativePath
        };
    }

    private async Task<SpeechAudioStageDecision> ProcessStageIfNeededAsync(
        SpeechAudioPreparationStageRequest request,
        StageRunRecord stageRun,
        ProjectArtifact selectedSourceArtifact,
        SpeechAudioStageDecision decision,
        AudioQualityAnalysisResult sourceAnalysis,
        Guid analysisArtifactId,
        IDictionary<Guid, ProjectArtifact> processedArtifacts,
        List<Guid> registeredArtifactIds,
        CancellationToken cancellationToken)
    {
        if (!decision.RequiresProcessing)
        {
            return decision;
        }

        logger?.LogWarning(
            $"Speech audio preparation selected profile '{decision.ProfileId}' for {decision.Stage} from {decision.SourceKind}.");

        string stageName = decision.Stage.ToString().ToLowerInvariant();
        string processedRelativePath = ProjectArtifactPaths.GetSpeechProcessedAudioRelativePath(stageRun.Id, stageName);
        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(processedRelativePath));

        SpeechAudioProcessingResult processingResult = await processingService
            .ProcessAsync(
                new SpeechAudioProcessingRequest(
                    artifactStore.GetPath(selectedSourceArtifact.RelativePath),
                    tx.TemporaryPath,
                    new SpeechAudioFilterSelection(
                        decision.ProfileId,
                        decision.ProfileVersion,
                        decision.CatalogVersion,
                        decision.FilterChain,
                        decision.ProfileHash,
                        IsAutoSelectable: true,
                        IsBenchmarkOnly: false)),
                cancellationToken)
            .ConfigureAwait(false);

        AudioQualityAnalysisResult processedAnalysis = await audioQualityAnalyzer
            .AnalyzeAsync(
                new AudioQualityAnalysisRequest(
                    processingResult.OutputPath,
                    decision.SourceKind,
                    AudioQualityAnalysisThresholds.ForSource(decision.SourceKind)),
                cancellationToken)
            .ConfigureAwait(false);
        processedAnalysis = processedAnalysis with
        {
            AudioPath = processedRelativePath
        };

        string? guardrailFailure = GetGuardrailFailure(decision, sourceAnalysis, processedAnalysis);
        if (guardrailFailure is not null)
        {
            logger?.LogWarning(
                "Discarding processed audio for {Stage}: {GuardrailFailure}",
                decision.Stage,
                guardrailFailure);

            if (degradationWriter is not null)
            {
                try
                {
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            StageNames.AudioPreparation,
                            "AUDIO_PREPARATION_GUARDRAIL_FAILURE",
                            $"Processed audio for {decision.Stage} failed quality guardrails: {guardrailFailure}",
                            Detail: $"Profile: {decision.ProfileId}, Source: {decision.SourceKind}",
                            SelectedFallback: "Original audio",
                            RecommendedAction: null,
                            DateTimeOffset.UtcNow,
                            stageRun.Id),
                        request.ProjectId,
                        request.MediaAsset.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Failed to write degradation record for audio preparation guardrail failure.", ex);
                }
            }
            return decision with
            {
                RequiresProcessing = false,
                FallbackReason = guardrailFailure,
                ProcessedAnalysis = processedAnalysis
            };
        }

        await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);
        FileFingerprint fingerprint = await fileFingerprintService
            .ComputeAsync(artifactStore.GetPath(processedRelativePath), cancellationToken)
            .ConfigureAwait(false);

        ProjectArtifact artifact = CreateProcessedArtifact(
            request,
            stageRun,
            selectedSourceArtifact,
            processedRelativePath,
            fingerprint,
            processingResult,
            decision,
            analysisArtifactId);
        await mediaAssetRepository.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
        registeredArtifactIds.Add(artifact.Id);
        processedArtifacts[artifact.Id] = artifact;

        return decision with
        {
            OutputArtifactId = artifact.Id,
            OutputRelativePath = artifact.RelativePath,
            ProcessedAnalysis = processedAnalysis
        };
    }

    private static string? GetGuardrailFailure(
        SpeechAudioStageDecision decision,
        AudioQualityAnalysisResult before,
        AudioQualityAnalysisResult after)
    {
        double durationDrift = Math.Abs(after.Metrics.DurationSeconds - before.Metrics.DurationSeconds);
        if (durationDrift > AudioQualityPolicy.ProcessedDurationDriftRejectSeconds)
        {
            return $"duration drift {durationDrift:F3}s exceeded {AudioQualityPolicy.ProcessedDurationDriftRejectSeconds:F3}s";
        }

        double clippingIncrease = after.Metrics.ClippedSamplePercent - before.Metrics.ClippedSamplePercent;
        if (clippingIncrease > AudioQualityPolicy.ProcessedClippingIncreaseRejectPercent)
        {
            return $"clipping increased by {clippingIncrease:F3}%";
        }

        if (after.Metrics.ActiveRmsDbfs > AudioQualityPolicy.ProcessedActiveRmsRejectDbfs)
        {
            return $"active RMS {after.Metrics.ActiveRmsDbfs:F1} dBFS is too hot";
        }

        double speechBandWorsening = before.Metrics.SpeechBandRatioDb - after.Metrics.SpeechBandRatioDb;
        if (speechBandWorsening > AudioQualityPolicy.ProcessedSpeechBandWorsenRejectDb)
        {
            return $"speech-band ratio worsened by {speechBandWorsening:F1} dB";
        }

        bool denoiseProfile = decision.ProfileId.Contains("denoise", StringComparison.OrdinalIgnoreCase) ||
                              decision.FilterChain.Contains("afftdn", StringComparison.OrdinalIgnoreCase);
        if (denoiseProfile &&
            before.Metrics.SnrDb is double beforeSnr &&
            after.Metrics.SnrDb is double afterSnr &&
            before.Metrics.SnrConfidence is AudioSnrConfidence.Reliable &&
            after.Metrics.SnrConfidence is not AudioSnrConfidence.Unavailable &&
            afterSnr - beforeSnr < AudioQualityPolicy.DenoiseMinimumSnrImprovementDb)
        {
            return $"SNR improved by only {afterSnr - beforeSnr:F1} dB";
        }

        return null;
    }

    private static ProjectArtifact ResolveArtifact(
        ProjectArtifact rawArtifact,
        SpeechAudioStageDecision decision,
        IReadOnlyDictionary<Guid, ProjectArtifact> processedArtifacts) =>
        decision.OutputArtifactId is Guid outputArtifactId && processedArtifacts.TryGetValue(outputArtifactId, out ProjectArtifact? processedArtifact)
            ? processedArtifact
            : rawArtifact;

    private static ProjectArtifact CreateAnalysisArtifact(
        SpeechAudioPreparationStageRequest request,
        StageRunRecord stageRun,
        SpeechAudioPreparationPlan plan,
        Guid artifactId,
        string relativePath,
        FileFingerprint fingerprint) =>
        new(
            artifactId,
            request.ProjectId,
            request.MediaAsset.Id,
            ArtifactKind.AudioQualityAnalysis,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            DurationSeconds: null,
            SampleRate: null,
            ChannelCount: null,
            DateTimeOffset.UtcNow,
            StageRunId: stageRun.Id,
            Provenance: $"audio-quality-analysis:policy={AudioQualityPolicy.AnalyzerPolicyVersion};catalog={SpeechAudioProcessingProfileCatalog.CatalogVersion};selectedSource={plan.SelectedSourceKind}");

    private static ProjectArtifact CreateProcessedArtifact(
        SpeechAudioPreparationStageRequest request,
        StageRunRecord stageRun,
        ProjectArtifact sourceArtifact,
        string relativePath,
        FileFingerprint fingerprint,
        SpeechAudioProcessingResult result,
        SpeechAudioStageDecision decision,
        Guid analysisArtifactId) =>
        new(
            Guid.NewGuid(),
            request.ProjectId,
            request.MediaAsset.Id,
            ArtifactKind.SpeechProcessedAudio,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            result.DurationSeconds,
            result.SampleRate,
            result.ChannelCount,
            DateTimeOffset.UtcNow,
            StageRunId: stageRun.Id,
            Provenance: $"speech-processing:source={sourceArtifact.Id:D};stage={decision.Stage.ToString().ToLowerInvariant()};profile={decision.ProfileId};version={decision.ProfileVersion};hash={decision.ProfileHash};analysis={analysisArtifactId:D}");

    private static string FormatNullable(double? value) =>
        value is null ? "n/a" : $"{value.Value:F1}dB";
}

public sealed record SpeechAudioPreparationStageRequest(
    Guid ProjectId,
    MediaAsset MediaAsset,
    ProjectArtifact NormalizedAudioArtifact,
    IReadOnlyList<ProjectArtifact> ExistingArtifacts);

public sealed record SpeechAudioPreparationAudit(
    Guid ProjectId,
    Guid MediaAssetId,
    string AnalyzerPolicyVersion,
    string ProfileCatalogVersion,
    SpeechAudioSourceAudit FullMix,
    SpeechAudioSourceKind SelectedSourceKind,
    IReadOnlyList<SpeechAudioDecisionAudit> Decisions)
{
    public static SpeechAudioPreparationAudit Create(
        SpeechAudioPreparationStageRequest request,
        SpeechAudioPreparationPlan plan,
        params SpeechAudioStageDecision[] decisions) =>
        new(
            request.ProjectId,
            request.MediaAsset.Id,
            AudioQualityPolicy.AnalyzerPolicyVersion,
            SpeechAudioProcessingProfileCatalog.CatalogVersion,
            SpeechAudioSourceAudit.FromArtifact(request.NormalizedAudioArtifact, plan.FullMixAnalysis),
            plan.SelectedSourceKind,
            decisions.Select(SpeechAudioDecisionAudit.FromDecision).ToArray());
}

public sealed record SpeechAudioSourceAudit(
    Guid SourceArtifactId,
    string SourceRelativePath,
    string SourceSha256,
    ArtifactKind SourceKind,
    AudioQualityAnalysisResult Analysis)
{
    public static SpeechAudioSourceAudit FromArtifact(ProjectArtifact artifact, AudioQualityAnalysisResult analysis) =>
        new(artifact.Id, artifact.RelativePath, artifact.Sha256, artifact.Kind, analysis);
}

public sealed record SpeechAudioDecisionAudit(
    SpeechPipelineStageKind Stage,
    string ProfileId,
    int ProfileVersion,
    string ProfileHash,
    string FilterChain,
    bool RequiresProcessing,
    IReadOnlyList<AudioQualityDefectKind> TriggeredDefects,
    Guid? OutputArtifactId,
    string? OutputRelativePath,
    string? FallbackReason,
    AudioQualityAnalysisResult? ProcessedAnalysis)
{
    public static SpeechAudioDecisionAudit FromDecision(SpeechAudioStageDecision decision) =>
        new(
            decision.Stage,
            decision.ProfileId,
            decision.ProfileVersion,
            decision.ProfileHash,
            decision.FilterChain,
            decision.RequiresProcessing,
            decision.TriggeredDefects,
            decision.OutputArtifactId,
            decision.OutputRelativePath,
            decision.FallbackReason,
            decision.ProcessedAnalysis);
}
