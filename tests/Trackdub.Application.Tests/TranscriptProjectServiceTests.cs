using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;
using System.Security.Cryptography;

#pragma warning disable CS0618

namespace Trackdub.Application.Tests;

public sealed partial class TranscriptProjectServiceTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    public void Dispose()
    {
        foreach (string directory in tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private async Task<(FakeServiceScope Scope, TranscriptProjectState State)> CreateWorkspaceProjectAsync(
        bool enableSpeakerDiarization = true,
        FakeTtsEngine? ttsEngine = null,
        ILoudnessNormalizer? loudnessNormalizer = null)
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Join(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory, ttsEngine: ttsEngine, loudnessNormalizer: loudnessNormalizer);
        TranscriptProjectState state = await scope.Workspace.Project.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: enableSpeakerDiarization),
            TestContext.Current.CancellationToken);

        return (scope, state);
    }

    private static AudioQualityMetrics CreateAudioQualityMetrics(SpeechAudioSourceKind sourceKind) =>
        new(
            DurationSeconds: 12.0d,
            PeakDbfs: -6.0d,
            RmsDbfs: -24.0d,
            ActiveRmsDbfs: -20.0d,
            Lufs: null,
            AudioQualityAnalysisConfidence.High,
            sourceKind,
            ClippedSamplePercent: 0.0d,
            NearSilencePercent: 0.0d,
            DcOffset: 0.0d,
            RumbleRatioDb: -30.0d,
            HissRatioDb: -30.0d,
            SpeechBandRatioDb: -3.0d,
            CrestFactorDb: 18.0d,
            DynamicRangeDb: 12.0d,
            NoiseFloorDbfs: -50.0d,
            SnrDb: 30.0d,
            AudioSnrConfidence.Reliable);

    private static void AssertProjectUiSettings(ProjectUiSettings expected, ProjectUiSettings? actual)
    {
        Assert.NotNull(actual);
        Assert.NotNull(actual!.Mix);
        Assert.NotNull(actual.Export);
        Assert.Equal(expected.Mix, actual.Mix);
        Assert.Equal(expected.Export!.SubtitleFormats, actual.Export!.SubtitleFormats);
        Assert.Equal(expected.Export.SubtitleSource, actual.Export.SubtitleSource);
        Assert.Equal(expected.Export.BurnInSubtitles, actual.Export.BurnInSubtitles);
        Assert.Equal(expected.Export.TargetLufs, actual.Export.TargetLufs);
        Assert.Equal(expected.Export.Container, actual.Export.Container);
        Assert.Equal(expected.Export.MatchOriginalLoudness, actual.Export.MatchOriginalLoudness);
    }

    // Separation is no longer part of import; tests that need stems run the stage after creating.
    private static async Task<TranscriptProjectState> CreateWithStemsAsync(
        FakeServiceScope scope,
        CreateTranscriptProjectRequest request)
    {
        await scope.Service.CreateAsync(request, TestContext.Current.CancellationToken);
        return await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);
    }

    private FakeServiceScope CreateScope(
        string tempDirectory,
        ISpeakerDiarizationEngine? diarizationEngine = null,
        FakeTtsEngine? ttsEngine = null,
        DiarizationStageHandler? diarizationStageHandler = null,
        IAudioTranscriptionEngine? transcriptionEngine = null,
        FakeStemSeparationEngine? stemSeparationEngine = null,
        FakeSpeechRegionDetector? speechRegionDetector = null,
        FakeSpeechAudioEnhancementService? speechAudioEnhancementService = null,
        FakeAudioQualityAnalyzer? audioQualityAnalyzer = null,
        FakeSpeechAudioProcessingService? speechAudioProcessingService = null,
        FakeAudioTimeStretchService? audioTimeStretchService = null,
        FakeTtsAudioPostProcessor? ttsAudioPostProcessor = null,
        FakeReferenceClipAnalyzer? referenceClipAnalyzer = null,
        FakeReferenceClipTrimmer? referenceClipTrimmer = null,
        FakeAudioClipExtractor? audioClipExtractor = null,
        IFileFingerprintService? fileFingerprintService = null,
        TtsTimingOptions? timingOptions = null,
        IExportToolAvailabilityService? exportToolAvailabilityService = null,
        FakeTranslationEngine? translationEngine = null,
        bool enableSpeechEnhancement = false,
        ILoudnessNormalizer? loudnessNormalizer = null)
    {
        var mediaRepository = new FakeMediaAssetRepository();
        var speakerRepository = new FakeSpeakerRepository();
        var artifactStore = new FakeArtifactStore(Path.Join(tempDirectory, "project"));
        var transcriptRepository = new FakeTranscriptRepository(speakerRepository);
        var translationRepository = new FakeTranslationRepository();
        var voiceAssignmentRepository = new FakeVoiceAssignmentRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var translationLanguageRouter = new FakeTranslationLanguageRouter();
        var translationLanguageRouterForReopen = new FakeTranslationLanguageRouter();
        translationEngine ??= new FakeTranslationEngine();
        diarizationEngine ??= new FakeDiarizationEngine();
        diarizationStageHandler ??= new DiarizationStageHandler(
            diarizationEngine,
            new RecordingModelDownloader(),
            modelCacheRoot: Path.Join(tempDirectory, "model-cache"),
            expectedSha256: SortFormerTestFixtures.ExpectedSha256);
        transcriptionEngine ??= new FakeAudioTranscriptionEngine();
        ttsEngine ??= new FakeTtsEngine();
        stemSeparationEngine ??= new FakeStemSeparationEngine();
        speechRegionDetector ??= new FakeSpeechRegionDetector();
        speechAudioEnhancementService ??= new FakeSpeechAudioEnhancementService();
        audioQualityAnalyzer ??= new FakeAudioQualityAnalyzer();
        speechAudioProcessingService ??= new FakeSpeechAudioProcessingService();
        audioTimeStretchService ??= new FakeAudioTimeStretchService();
        ttsAudioPostProcessor ??= new FakeTtsAudioPostProcessor();
        referenceClipAnalyzer ??= new FakeReferenceClipAnalyzer();
        referenceClipTrimmer ??= new FakeReferenceClipTrimmer();
        audioClipExtractor ??= new FakeAudioClipExtractor();
        TtsTimingOptions normalizedTimingOptions = timingOptions ?? TtsTimingOptions.Default;
        var voiceCatalog = new FakeVoiceCatalog();
        fileFingerprintService ??= new FakeFileFingerprintService();
        var fileSystemProbe = new FakeFileSystemProbe { TreatAllFilesAsExisting = true };
        var stemSeparationStageHandler = new StemSeparationStageHandler(
            stemSeparationEngine,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            stageRunStore);
        var speechAudioPreparationStageHandler = new SpeechAudioPreparationStageHandler(
            audioQualityAnalyzer,
            new SpeechAudioPreparationPlanner(),
            speechAudioProcessingService,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            stageRunStore);
        var degradationWriter = new PipelineDegradationWriter(
            artifactStore,
            fileFingerprintService,
            mediaRepository);
        var speechAudioEnhancementStageHandler = new SpeechAudioEnhancementStageHandler(
            speechAudioEnhancementService,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            stageRunStore);
        var projectMediaIngestService = new ProjectMediaIngestService(
            new FakeProjectRepository(),
            mediaRepository,
            artifactStore,
            new FakeMediaProbe(),
            new FakeAudioExtractionService(),
            new FakeWaveformSummaryGenerator(),
            fileFingerprintService,
            fileSystemProbe);
        var artifactWriter = new TranscriptArtifactWriter(
            artifactStore,
            fileFingerprintService,
            mediaRepository);
        var segmentEditingService = new SegmentEditingService(
            transcriptRepository,
            ttsTakeRepository,
            artifactWriter);
        var voiceAssignmentService = new VoiceAssignmentService(
            voiceAssignmentRepository,
            ttsTakeRepository,
            voiceCatalog);
        var durationAnalysisService = new DurationAnalysisService();
        var startTtsStageHandler = new StartTtsStageHandler(
            ttsEngine,
            voiceCatalog,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            ttsTakeRepository,
            stageRunStore,
            durationAnalysisService,
            audioTimeStretchService,
            normalizedTimingOptions,
            ttsAudioPostProcessor,
            commitBoundary: TestAtomicCommitBoundary.Create(null, ttsTakeRepository, artifactStore, mediaRepository));
        var ttsOrchestrationService = new TtsOrchestrationService(
            startTtsStageHandler,
            voiceAssignmentRepository,
            ttsTakeRepository,
            ttsEngine,
            voiceCatalog,
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            referenceClipTrimmer,
            durationAnalysisService,
            audioTimeStretchService,
            normalizedTimingOptions,
            audioClipExtractor,
            referenceClipAnalyzer);
        var referenceClipService = new SpeakerReferenceClipService(
            artifactStore,
            audioClipExtractor,
            fileFingerprintService,
            mediaRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            referenceClipAnalyzer,
            referenceClipTrimmer);
        var speakerAssignmentService = new SpeakerAssignmentService(
            speakerRepository,
            transcriptRepository,
            segmentEditingService,
            artifactStore,
            stageRunStore,
            diarizationEngine,
            referenceClipService,
            artifactWriter,
            diarizationStageHandler);
        var translationOrchestrationService = new TranslationOrchestrationService(
            translationRepository,
            new GlossaryService(new FakeGlossaryRepository()),
            new GlossaryTermMatcher(),
            translationLanguageRouter,
            translationEngine,
            ttsTakeRepository,
            stageRunStore,
            artifactStore,
            artifactWriter,
            commitBoundary: TestAtomicCommitBoundary.Create(translationRepository, ttsTakeRepository, artifactStore, mediaRepository));
        var stateService = new TranscriptProjectStateService(
            projectMediaIngestService,
            transcriptRepository,
            translationRepository,
            stageRunStore,
            speakerRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            translationLanguageRouter,
            voiceCatalog,
            artifactStore,
            ttsOrchestrationService,
            voiceAssignmentService,
            exportToolAvailabilityService: exportToolAvailabilityService);
        var asrStageHandler = new AsrStageHandler(transcriptionEngine, stageRunStore);
        var transcriptGenerationService = new TranscriptGenerationService(
            transcriptRepository,
            artifactStore,
            asrStageHandler,
            artifactWriter,
            new VadGenerationStage(new VadStageHandler(speechRegionDetector, stageRunStore), artifactWriter, artifactStore),
            new FakeEnhancementStage(),
            new SpeakerDiarizationStage(speakerAssignmentService, artifactWriter, artifactStore, stageRunStore),
            new AsrGenerationStage(asrStageHandler, artifactStore, stageRunStore, degradationWriter),
            new TextRefinementGenerationStage(
                new TextRefinementStageHandler(new FakeTextRefinementEngine(), stageRunStore),
                stageRunStore),
            new SpeakerAssignmentAndPersistenceStage(speakerAssignmentService, transcriptRepository, artifactWriter, artifactStore, stageRunStore),
            new FakePipelinePreFlightChecker(),
            stageRunStore,
            mediaRepository,
            speakerRepository);
        var projectWorkflow = new ProjectWorkflow(
            projectMediaIngestService,
            stateService,
            transcriptGenerationService,
            stemSeparationStageHandler,
            speechAudioPreparationStageHandler,
            degradationWriter,
            enableSpeechEnhancement ? speechAudioEnhancementStageHandler : null);
        var previewMixWorkflow = new PreviewMixWorkflow(
            new MixPlanBuilder(),
            new MixPlanStore(artifactStore),
            new FakeMixRenderer(),
            artifactStore,
            fileFingerprintService,
            mediaRepository,
            stageRunStore);
        var exportWorkflow = new ExportWorkflow(
            stateService,
            new ExportStageHandler(
                new MixPlanBuilder(),
                new MixPlanStore(artifactStore),
                new FakeMixRenderer(),
                artifactStore,
                fileFingerprintService,
                mediaRepository,
                stageRunStore,
                loudnessNormalizer ?? new FakeLoudnessNormalizer(),
                new FakeExportRenderer(),
                new FakeMediaProbe(),
                new SubtitleExportService(),
                new FakeVideoRecomposer()));
        var workspace = new TranscriptWorkspace(
            projectWorkflow,
            new DiarizationModelWorkflow(diarizationStageHandler),
            new TranscriptWorkflow(
                stateService,
                segmentEditingService,
                transcriptGenerationService),
            new TranslationWorkflow(
                stateService,
                translationOrchestrationService),
            new SpeakerWorkflow(
                stateService,
                speakerAssignmentService),
            new VoiceWorkflow(
                stateService,
                voiceAssignmentService,
                ttsOrchestrationService),
            new TtsWorkflow(
                stateService,
                ttsOrchestrationService),
            new TtsDubPreviewWorkflow(
                stateService,
                new TtsDubPreviewCoordinator(new FakeAudioPreviewTransport(), artifactStore)),
            previewMixWorkflow,
            exportWorkflow,
            new EditingHistoryWorkflow(
                stateService,
                speakerAssignmentService,
                voiceAssignmentService,
                segmentEditingService,
                translationOrchestrationService));
        var service = new TranscriptProjectService(workspace);

        return new FakeServiceScope(
            service,
            workspace,
            artifactStore,
            mediaRepository,
            transcriptRepository,
            translationRepository,
            translationLanguageRouter,
            speakerRepository,
            voiceAssignmentRepository,
            ttsTakeRepository,
            ttsEngine,
            stemSeparationEngine,
            speechRegionDetector,
            speechAudioEnhancementService,
            audioQualityAnalyzer,
            speechAudioProcessingService,
            audioTimeStretchService,
            audioClipExtractor,
            referenceClipTrimmer,
            projectMediaIngestService,
            stageRunStore,
            translationLanguageRouterForReopen,
            voiceCatalog,
            ttsOrchestrationService,
            voiceAssignmentService,
            exportToolAvailabilityService);
    }

    private string CreateTempDirectory()
    {
        string tempDirectory = Path.Join(Path.GetTempPath(), "Trackdub.Application.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        tempDirectories.Add(tempDirectory);
        return tempDirectory;
    }

    private static FixedAudioTranscriptionEngine CreateSingleSegmentTranscription() =>
        new(
        [
            new RecognizedTranscriptSegment(
                Index: 0,
                StartSeconds: 0.0d,
                EndSeconds: 2.0d,
                Text: "Generated segment 1.",
                DetectedLanguage: "en")
        ]);

    private sealed class BlockingTtsEngine : FakeTtsEngine
    {
        private readonly TaskCompletionSource synthesisStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            synthesisStarted.TrySetResult();
            await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        }

        public Task WaitForSynthesisStartedAsync() => synthesisStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        public void Release() => release.TrySetResult();
    }

    private sealed record FakeServiceScope(
        TranscriptProjectService Service,
        TranscriptWorkspace Workspace,
        FakeArtifactStore ArtifactStore,
        FakeMediaAssetRepository MediaAssetRepository,
        FakeTranscriptRepository TranscriptRepository,
        FakeTranslationRepository TranslationRepository,
        FakeTranslationLanguageRouter TranslationLanguageRouter,
        FakeSpeakerRepository SpeakerRepository,
        FakeVoiceAssignmentRepository VoiceAssignmentRepository,
        FakeTtsTakeRepository TtsTakeRepository,
        FakeTtsEngine TtsEngine,
        FakeStemSeparationEngine StemSeparationEngine,
        FakeSpeechRegionDetector SpeechRegionDetector,
        FakeSpeechAudioEnhancementService SpeechAudioEnhancementService,
        FakeAudioQualityAnalyzer AudioQualityAnalyzer,
        FakeSpeechAudioProcessingService SpeechAudioProcessingService,
        FakeAudioTimeStretchService AudioTimeStretchService,
        FakeAudioClipExtractor AudioClipExtractor,
        FakeReferenceClipTrimmer ReferenceClipTrimmer,
        ProjectMediaIngestService ProjectMediaIngestService,
        FakeProjectStageRunStore StageRunStore,
        FakeTranslationLanguageRouter TranslationLanguageRouterForReopen,
        FakeVoiceCatalog VoiceCatalog,
        TtsOrchestrationService TtsOrchestrationService,
        VoiceAssignmentService VoiceAssignmentService,
        IExportToolAvailabilityService? ExportToolAvailabilityService)
    {
        public TranscriptProjectStateService CreateReopenedStateService() =>
            new(
                ProjectMediaIngestService,
                TranscriptRepository,
                TranslationRepository,
                StageRunStore,
                SpeakerRepository,
                VoiceAssignmentRepository,
                TtsTakeRepository,
                TranslationLanguageRouterForReopen,
                VoiceCatalog,
                ArtifactStore,
                TtsOrchestrationService,
                VoiceAssignmentService,
                exportToolAvailabilityService: ExportToolAvailabilityService);
    }

    private sealed class MutableExportToolAvailabilityService(ExportToolAvailability availability) : IExportToolAvailabilityService
    {
        public ExportToolAvailability Availability { get; set; } = availability;

        public int CheckCount { get; private set; }

        public ExportToolAvailability CheckAvailability()
        {
            CheckCount++;
            return Availability;
        }
    }

    private sealed class FakeProjectRepository : IProjectRepository
    {
        private TrackdubProject? project;

        public Task InitializeAsync(TrackdubProject project, CancellationToken cancellationToken)
        {
            this.project = project;
            return Task.CompletedTask;
        }

        public Task UpdateAsync(TrackdubProject project, CancellationToken cancellationToken)
        {
            this.project = project;
            return Task.CompletedTask;
        }

        public Task<TrackdubProject?> GetAsync(CancellationToken cancellationToken) => Task.FromResult(project);
    }

    private sealed class FakeMediaAssetRepository : IMediaAssetRepository
    {
        private MediaAsset? mediaAsset;

        public List<ProjectArtifact> Artifacts { get; } = [];

        public bool ThrowOnDeleteArtifact { get; set; }

        public Task SaveAsync(MediaAsset asset, CancellationToken cancellationToken)
        {
            mediaAsset = asset;
            return Task.CompletedTask;
        }

        public Task UpdateSourcePathAsync(
            Guid mediaAssetId,
            string sourceFilePath,
            string sourceFileName,
            CancellationToken cancellationToken)
        {
            if (mediaAsset is not null && mediaAsset.Id == mediaAssetId)
            {
                mediaAsset = mediaAsset with
                {
                    SourceFilePath = sourceFilePath,
                    SourceFileName = sourceFileName
                };
            }

            return Task.CompletedTask;
        }

        public Task<MediaAsset?> GetPrimaryAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult(mediaAsset);

        public Task SaveArtifactAsync(ProjectArtifact artifact, CancellationToken cancellationToken)
        {
            int index = Artifacts.FindIndex(candidate => candidate.Id == artifact.Id);
            if (index >= 0)
            {
                Artifacts[index] = artifact;
            }
            else
            {
                Artifacts.Add(artifact);
            }

            return Task.CompletedTask;
        }

        public Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            if (ThrowOnDeleteArtifact)
            {
                throw new InvalidOperationException("Artifact metadata cleanup failed.");
            }

            Artifacts.RemoveAll(artifact => artifact.Id == artifactId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProjectArtifact>> GetArtifactsAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectArtifact>>(Artifacts.OrderBy(artifact => artifact.CreatedAtUtc).ToArray());

        public Task<IReadOnlyList<MediaAsset>> GetAllAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MediaAsset>>(mediaAsset is not null && mediaAsset.ProjectId == projectId ? new List<MediaAsset> { mediaAsset } : new List<MediaAsset>());

        public Task<ProjectArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            ProjectArtifact? artifact = Artifacts.FirstOrDefault(a => a.Id == artifactId);
            return Task.FromResult(artifact);
        }
    }

    private sealed class ThrowingReferenceClipFingerprintService : IFileFingerprintService
    {
        private readonly FakeFileFingerprintService inner = new();

        public Task<FileFingerprint> ComputeAsync(string path, CancellationToken cancellationToken)
        {
            string normalizedPath = path.Replace('\\', '/');
            if (normalizedPath.Contains(ProjectArtifactPaths.ReferenceClipDirectoryRelativePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Reference clip fingerprint failed.");
            }

            return inner.ComputeAsync(path, cancellationToken);
        }
    }

    private sealed class FakeArtifactStore(string rootPath) : IArtifactStore
    {
        private readonly Dictionary<string, object> reads = new(StringComparer.OrdinalIgnoreCase);

        public Task EnsureLayoutAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(rootPath);
            foreach (string relativeDirectory in ProjectArtifactPaths.RequiredDirectories)
            {
                Directory.CreateDirectory(GetPath(relativeDirectory));
            }

            return Task.CompletedTask;
        }

        public ArtifactWriteHandle CreateWriteHandle(string relativePath)
        {
            string finalPath = GetPath(relativePath);
            string tempPath = Path.Join(GetPath("temp"), $"{Guid.NewGuid():N}-{Path.GetFileName(relativePath)}");
            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
            return new ArtifactWriteHandle(relativePath, finalPath, tempPath);
        }

        public Task CommitAsync(ArtifactWriteHandle handle, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(handle.FinalPath)!);
            File.Move(handle.TemporaryPath, handle.FinalPath, overwrite: true);
            return Task.CompletedTask;
        }

        public async Task WriteJsonAsync<T>(string relativePath, T value, CancellationToken cancellationToken)
        {
            string path = GetPath(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(value), cancellationToken);
            reads[relativePath] = value!;
        }

        public async Task<T?> ReadJsonAsync<T>(string relativePath, CancellationToken cancellationToken)
        {
            if (reads.TryGetValue(relativePath, out object? value))
            {
                return (T?)value;
            }

            // Fall back to disk so a cleared cache genuinely rehydrates from durable state.
            string path = GetPath(relativePath);
            if (!File.Exists(path))
            {
                return default;
            }

            string json = await File.ReadAllTextAsync(path, cancellationToken);
            T? deserialized = System.Text.Json.JsonSerializer.Deserialize<T>(json);
            if (deserialized is not null)
            {
                reads[relativePath] = deserialized;
            }

            return deserialized;
        }

        /// <summary>
        /// Clears the in-memory JSON cache so subsequent reads come from disk,
        /// simulating a process restart for reopen coverage.
        /// </summary>
        public void SimulateProcessRestart() => reads.Clear();

        public void Remove(string relativePath)
        {
            reads.Remove(relativePath);
            string path = GetPath(relativePath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        public string GetPath(string relativePath) => Path.GetFullPath(Path.Join(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public bool Exists(string relativePath) => File.Exists(GetPath(relativePath));
    }

    private sealed class FakeMediaProbe : IMediaProbe
    {
        public Task<MediaProbeSnapshot> ProbeAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromResult(new MediaProbeSnapshot(
                "mov,mp4",
                "QuickTime / MOV",
                12.0,
                1024,
                [new MediaAudioStream(0, "aac", 2, 44100, 12.0)],
                [new MediaVideoStream(1, "h264", 1920, 1080, 24, 12.0)]));
    }

    private sealed class FakeAudioExtractionService : IAudioExtractionService
    {
        public async Task<AudioExtractionResult> ExtractNormalizedAudioAsync(
            string sourcePath,
            string destinationPath,
            CancellationToken cancellationToken,
            int? maxEncoderThreads = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, FakeWavHelper.MinimalPcm16(durationSeconds: 12.0, sampleRate: 48000, channelCount: 2), cancellationToken);
            return new AudioExtractionResult(destinationPath, 12.0, 48000, 2, 576000);
        }

        public async Task<AudioExtractionResult> ExtractStemSeparationAudioAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, FakeWavHelper.MinimalPcm16(durationSeconds: 12.0, sampleRate: 44100, channelCount: 2), cancellationToken);
            return new AudioExtractionResult(destinationPath, 12.0, 44100, 2, 529200);
        }
    }

    private sealed class FakeAudioClipExtractor : IAudioClipExtractor
    {
        public string? LastSourceWavePath { get; private set; }

        public async Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            double startSeconds,
            double endSeconds,
            string destinationPath,
            CancellationToken cancellationToken) =>
            await ExtractAsync(
                sourceWavePath,
                [new AudioClipRange(startSeconds, endSeconds)],
                destinationPath,
                cancellationToken).ConfigureAwait(false);

        public async Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            IReadOnlyList<AudioClipRange> ranges,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            LastSourceWavePath = sourceWavePath;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [11, 22, 33, 44], cancellationToken);
            double durationSeconds = ranges.Sum(range => range.EndSeconds - range.StartSeconds);
            return new AudioClipExtractionResult(destinationPath, durationSeconds, 48000, 1);
        }
    }

    private sealed class FakeWaveformSummaryGenerator : IWaveformSummaryGenerator
    {
        public Task<WaveformSummary> GenerateAsync(string audioPath, CancellationToken cancellationToken) =>
            Task.FromResult(new WaveformSummary(4, 48000, 2, 12.0, [0.1f, 0.5f, 0.3f, 0.2f]));
    }

    private sealed class FakeTranscriptRepository : ITranscriptRepository
    {
        private readonly FakeSpeakerRepository? speakerRepository;

        public FakeTranscriptRepository()
        {
        }

        public FakeTranscriptRepository(FakeSpeakerRepository speakerRepository)
        {
            this.speakerRepository = speakerRepository;
        }

        public List<TranscriptRevision> Revisions { get; } = [];

        public Dictionary<Guid, IReadOnlyList<TranscriptSegment>> SegmentsByRevisionId { get; } = new();

        public Task<TranscriptRevision?> GetCurrentRevisionAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult(Revisions.Where(revision => revision.ProjectId == projectId).OrderByDescending(revision => revision.RevisionNumber).FirstOrDefault());

        public Task<IReadOnlyList<TranscriptSegment>> GetSegmentsAsync(Guid transcriptRevisionId, CancellationToken cancellationToken) =>
            Task.FromResult(SegmentsByRevisionId.TryGetValue(transcriptRevisionId, out IReadOnlyList<TranscriptSegment>? segments)
                ? segments
                : (IReadOnlyList<TranscriptSegment>)[]);

        public Task<int> GetNextRevisionNumberAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult(Revisions.Where(revision => revision.ProjectId == projectId).Select(revision => revision.RevisionNumber).DefaultIfEmpty(0).Max() + 1);

        public Task SaveRevisionAsync(TranscriptRevision revision, IReadOnlyList<TranscriptSegment> segments, CancellationToken cancellationToken)
        {
            Revisions.Add(revision);
            SegmentsByRevisionId[revision.Id] = segments;
            return Task.CompletedTask;
        }

        public Task ReassignSpeakerAsync(Guid projectId, Guid sourceSpeakerId, Guid targetSpeakerId, CancellationToken cancellationToken)
        {
            foreach (TranscriptRevision revision in Revisions.Where(revision => revision.ProjectId == projectId))
            {
                if (!SegmentsByRevisionId.TryGetValue(revision.Id, out IReadOnlyList<TranscriptSegment>? segments))
                {
                    continue;
                }

                SegmentsByRevisionId[revision.Id] = segments
                    .Select(segment => segment.SpeakerId == sourceSpeakerId
                        ? segment with { SpeakerId = targetSpeakerId }
                        : segment)
                    .ToArray();
            }

            return Task.CompletedTask;
        }

        public async Task ReassignAndMergeSpeakersAsync(Guid projectId, Guid sourceSpeakerId, Guid targetSpeakerId, CancellationToken cancellationToken)
        {
            await ReassignSpeakerAsync(projectId, sourceSpeakerId, targetSpeakerId, cancellationToken).ConfigureAwait(false);
            if (speakerRepository is not null)
            {
                speakerRepository.Turns.RemoveAll(turn => turn.ProjectId == projectId && turn.SpeakerId == sourceSpeakerId);
                speakerRepository.Speakers.RemoveAll(speaker => speaker.ProjectId == projectId && speaker.Id == sourceSpeakerId);
            }
        }
    }

    private sealed class FakeSpeakerRepository : ISpeakerRepository
    {
        public List<ProjectSpeaker> Speakers { get; } = [];

        public List<SpeakerTurn> Turns { get; } = [];

        public Task<IReadOnlyList<ProjectSpeaker>> ListSpeakersAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProjectSpeaker>>(Speakers.Where(speaker => speaker.ProjectId == projectId).OrderBy(speaker => speaker.CreatedAtUtc).ToArray());

        public Task<IReadOnlyList<MergedSpeakerEntry>> ListMergedSpeakersAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MergedSpeakerEntry>>([]);

        public Task DeleteEmptySpeakerAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken)
        {
            if (Turns.Exists(turn => turn.ProjectId == projectId && turn.SpeakerId == speakerId))
            {
                throw new InvalidOperationException("Only speakers with no turns, segments, or voice assignments can be removed.");
            }

            Speakers.RemoveAll(speaker => speaker.ProjectId == projectId && speaker.Id == speakerId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SpeakerTurn>> ListTurnsAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SpeakerTurn>>(Turns.Where(turn => turn.ProjectId == projectId).OrderBy(turn => turn.StartSeconds).ToArray());

        public Task<ProjectSpeaker> EnsureDefaultSpeakerAsync(Guid projectId, CancellationToken cancellationToken)
        {
            ProjectSpeaker? existing = Speakers.FirstOrDefault(speaker => speaker.ProjectId == projectId);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            ProjectSpeaker speaker = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
            Speakers.Add(speaker);
            return Task.FromResult(speaker);
        }

        public Task<ProjectSpeaker> CreateSpeakerAsync(Guid projectId, CancellationToken cancellationToken)
        {
            ProjectSpeaker speaker = ProjectSpeaker.Create(
                projectId,
                BuildNextSpeakerDisplayName(projectId),
                DateTimeOffset.UtcNow.AddMilliseconds(Speakers.Count(speaker => speaker.ProjectId == projectId)));
            Speakers.Add(speaker);
            return Task.FromResult(speaker);
        }

        public Task ReplaceDiarizationAsync(Guid projectId, IReadOnlyList<ProjectSpeaker> speakers, IReadOnlyList<SpeakerTurn> turns, CancellationToken cancellationToken)
        {
            bool preserveExistingSpeakers = Speakers.Any(speaker => speaker.ProjectId == projectId) &&
                                            !Turns.Any(turn => turn.ProjectId == projectId);
            if (!preserveExistingSpeakers)
            {
                Speakers.RemoveAll(speaker => speaker.ProjectId == projectId);
            }

            Speakers.AddRange(speakers);
            Turns.RemoveAll(turn => turn.ProjectId == projectId);
            Turns.AddRange(turns);
            return Task.CompletedTask;
        }

        public Task RenameSpeakerAsync(Guid projectId, Guid speakerId, string displayName, CancellationToken cancellationToken)
        {
            int index = Speakers.FindIndex(speaker => speaker.ProjectId == projectId && speaker.Id == speakerId);
            if (index >= 0)
            {
                Speakers[index] = Speakers[index].Rename(displayName);
            }

            return Task.CompletedTask;
        }

        public Task SplitTurnAsync(Guid projectId, Guid speakerTurnId, double splitSeconds, CancellationToken cancellationToken)
        {
            int index = Turns.FindIndex(turn => turn.ProjectId == projectId && turn.Id == speakerTurnId);
            if (index < 0)
            {
                throw new InvalidOperationException("Speaker turn was not found.");
            }

            SpeakerTurn original = Turns[index];
            Turns.RemoveAt(index);
            Turns.Insert(index, SpeakerTurn.Create(projectId, original.SpeakerId, original.StartSeconds, splitSeconds, original.Confidence, original.HasOverlap, original.StageRunId));
            Turns.Insert(index + 1, SpeakerTurn.Create(projectId, original.SpeakerId, splitSeconds, original.EndSeconds, original.Confidence, original.HasOverlap, original.StageRunId));
            return Task.CompletedTask;
        }

        private string BuildNextSpeakerDisplayName(Guid projectId)
        {
            const string prefix = "Speaker ";
            HashSet<string> names = Speakers
                .Where(speaker => speaker.ProjectId == projectId)
                .Select(speaker => speaker.DisplayName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            int nextNumber = names
                .Select(name => name.Trim())
                .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(name => int.TryParse(name[prefix.Length..], out int number) ? number : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

            string candidate;
            do
            {
                candidate = $"{prefix}{nextNumber++}";
            }
            while (names.Contains(candidate));

            return candidate;
        }
    }

    private sealed class FakeTranslationRepository : ITranslationRepository
    {
        public List<TranslationRevision> Revisions { get; } = [];

        public Dictionary<Guid, IReadOnlyList<TranslatedSegment>> SegmentsByRevisionId { get; } = new();

        public Task<TranslationRevision?> GetCurrentRevisionAsync(Guid projectId, string targetLanguage, CancellationToken cancellationToken) =>
            Task.FromResult(Revisions
                .Where(revision => revision.ProjectId == projectId && revision.TargetLanguage == targetLanguage)
                .OrderByDescending(revision => revision.RevisionNumber)
                .FirstOrDefault());

        public Task<IReadOnlyList<TranslatedSegment>> GetSegmentsAsync(Guid translationRevisionId, CancellationToken cancellationToken) =>
            Task.FromResult(SegmentsByRevisionId.TryGetValue(translationRevisionId, out IReadOnlyList<TranslatedSegment>? segments)
                ? segments
                : (IReadOnlyList<TranslatedSegment>)[]);

        public Task<int> GetNextRevisionNumberAsync(Guid projectId, string targetLanguage, CancellationToken cancellationToken) =>
            Task.FromResult(Revisions
                .Where(revision => revision.ProjectId == projectId && revision.TargetLanguage == targetLanguage)
                .Select(revision => revision.RevisionNumber)
                .DefaultIfEmpty(0)
                .Max() + 1);

        public Task SaveRevisionAsync(TranslationRevision revision, IReadOnlyList<TranslatedSegment> segments, CancellationToken cancellationToken)
        {
            Revisions.Add(revision);
            SegmentsByRevisionId[revision.Id] = segments;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVoiceAssignmentRepository : IVoiceAssignmentRepository
    {
        public List<VoiceAssignment> Assignments { get; } = [];

        public bool ThrowOnSave { get; set; }

        public Task<VoiceAssignment?> GetByIdAsync(Guid projectId, Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Assignments.FirstOrDefault(assignment => assignment.ProjectId == projectId && assignment.Id == id));

        public Task<VoiceAssignment?> GetAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken) =>
            Task.FromResult(Assignments.FirstOrDefault(assignment =>
                assignment.ProjectId == projectId &&
                assignment.SpeakerId == speakerId &&
                !assignment.IsFallback));

        public Task<VoiceAssignment?> GetForSpeakerIncludingMergedAsync(Guid projectId, Guid speakerId, CancellationToken cancellationToken) =>
            GetAsync(projectId, speakerId, cancellationToken);

        public Task<IReadOnlyList<VoiceAssignment>> GetAllAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VoiceAssignment>>(Assignments
                .Where(assignment => assignment.ProjectId == projectId && !assignment.IsFallback)
                .OrderBy(assignment => assignment.CreatedAtUtc)
                .ToArray());

        public Task SaveAsync(VoiceAssignment assignment, CancellationToken cancellationToken)
        {
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("Voice assignment save failed.");
            }

            int index = Assignments.FindIndex(candidate => candidate.Id == assignment.Id);
            if (index >= 0)
            {
                Assignments[index] = assignment;
                return Task.CompletedTask;
            }

            index = Assignments.FindIndex(candidate =>
                candidate.ProjectId == assignment.ProjectId &&
                candidate.SpeakerId == assignment.SpeakerId &&
                !candidate.IsFallback &&
                !assignment.IsFallback);
            if (index >= 0)
            {
                Assignments[index] = assignment;
                return Task.CompletedTask;
            }

            Assignments.Add(assignment);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken)
        {
            Assignments.RemoveAll(assignment => assignment.Id == id);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTtsTakeRepository : ITtsTakeRepository
    {
        public List<TtsTake> Takes { get; } = [];

        public Task<TtsTake?> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Takes.FirstOrDefault(take => take.Id == id));

        public Task<TtsTake?> GetByFingerprintAsync(Guid projectId, string inputFingerprint, CancellationToken cancellationToken) =>
            Task.FromResult(Takes
                .Where(take => take.ProjectId == projectId &&
                               take.InputFingerprint == inputFingerprint &&
                               !take.IsStale &&
                               take.Status == TtsTakeStatus.Completed)
                .OrderByDescending(take => take.CreatedAtUtc)
                .FirstOrDefault());

        public Task<IReadOnlyList<TtsTake>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TtsTake>>(Takes
                .Where(take => take.ProjectId == projectId)
                .OrderBy(take => take.CreatedAtUtc)
                .ToArray());

        public Task<IReadOnlyList<TtsTake>> GetBySegmentAsync(Guid translatedSegmentId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TtsTake>>(Takes
                .Where(take => take.TranslatedSegmentId == translatedSegmentId)
                .OrderBy(take => take.CreatedAtUtc)
                .ToArray());

        public Task<IReadOnlyList<TtsTake>> GetStaleBySpeakerAsync(
            Guid projectId,
            Guid voiceAssignmentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TtsTake>>(Takes
                .Where(take => take.ProjectId == projectId &&
                               take.VoiceAssignmentId == voiceAssignmentId &&
                               take.IsStale)
                .OrderBy(take => take.SegmentIndex)
                .ToArray());

        public Task MarkBySegmentIndicesStaleAsync(
            Guid projectId,
            IReadOnlySet<int> segmentIndices,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < Takes.Count; i++)
            {
                if (Takes[i].ProjectId == projectId && segmentIndices.Contains(Takes[i].SegmentIndex))
                {
                    Takes[i] = Takes[i].MarkStale();
                }
            }

            return Task.CompletedTask;
        }

        public Task MarkByVoiceAssignmentStaleAsync(
            Guid projectId,
            Guid voiceAssignmentId,
            CancellationToken cancellationToken)
        {
            for (int i = 0; i < Takes.Count; i++)
            {
                if (Takes[i].ProjectId == projectId && Takes[i].VoiceAssignmentId == voiceAssignmentId)
                {
                    Takes[i] = Takes[i].MarkStale();
                }
            }

            return Task.CompletedTask;
        }

        public Task SaveAsync(TtsTake take, CancellationToken cancellationToken)
        {
            int index = Takes.FindIndex(candidate => candidate.Id == take.Id);
            if (index >= 0)
            {
                Takes[index] = take;
            }
            else
            {
                Takes.Add(take);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeProjectStageRunStore : IProjectStageRunStore
    {
        private readonly List<StageRunRecord> stageRuns = [];

        public Task CreateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
        {
            stageRuns.Add(stageRun);
            return Task.CompletedTask;
        }

        public Task UpdateAsync(StageRunRecord stageRun, CancellationToken cancellationToken)
        {
            int index = stageRuns.FindIndex(candidate => candidate.Id == stageRun.Id);
            if (index >= 0)
            {
                stageRuns[index] = stageRun;
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<StageRunRecord>> ListByProjectAsync(Guid projectId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StageRunRecord>>(stageRuns.Where(stageRun => stageRun.ProjectId == projectId).OrderBy(stageRun => stageRun.StartedAtUtc).ToArray());
    }

    private sealed class FakeAudioTranscriptionEngine(IReadOnlyDictionary<int, double> confidenceByRegionIndex)
        : IAudioTranscriptionEngine
    {
        public FakeAudioTranscriptionEngine()
            : this(new Dictionary<int, double>
            {
                [0] = 0.92d,
                [1] = 0.60d
            })
        {
        }

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(regions
                .OrderBy(static region => region.Index)
                .Select((region, index) =>
                {
                    string text = $"Generated segment {index + 1}.";
                    double confidence = confidenceByRegionIndex.TryGetValue(region.Index, out double configuredConfidence)
                        ? configuredConfidence
                        : 0.92d;
                    return new RecognizedTranscriptSegment(
                        region.Index,
                        region.StartSeconds,
                        region.EndSeconds,
                        text,
                        DetectedLanguage: "en",
                        Words: BuildRecognizedWords(region, text, confidence));
                })
                .ToArray());
    }

    private sealed class FixedAudioTranscriptionEngine(IReadOnlyList<RecognizedTranscriptSegment> segments)
        : IAudioTranscriptionEngine
    {
        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult(segments);
    }

    private sealed class RecordingAudioTranscriptionEngine : IAudioTranscriptionEngine
    {
        public string? LastAudioPath { get; private set; }

        public string? LastSourceLanguage { get; private set; }

        public IReadOnlyList<SpeechRegion> LastRegions { get; private set; } = [];

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken)
        {
            LastSourceLanguage = null;
            return TranscribeCore(normalizedAudioPath, regions);
        }

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            LastSourceLanguage = request.SourceLanguage;
            return TranscribeCore(request.NormalizedAudioPath, request.Regions);
        }

        private Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeCore(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions)
        {
            LastAudioPath = normalizedAudioPath;
            LastRegions = regions.ToArray();
            return Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(regions
                .OrderBy(static region => region.Index)
                .Select((region, index) =>
                {
                    string text = $"Recorded segment {index + 1}.";
                    return new RecognizedTranscriptSegment(
                        region.Index,
                        region.StartSeconds,
                        region.EndSeconds,
                        text,
                        DetectedLanguage: "en",
                        Words: BuildRecognizedWords(region, text, 0.88d));
                })
                .ToArray());
        }
    }

    private static IReadOnlyList<RecognizedTranscriptWord> BuildRecognizedWords(
        SpeechRegion region,
        string text,
        double confidence)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return [];
        }

        double step = (region.EndSeconds - region.StartSeconds) / words.Length;
        return words
            .Select((word, index) => new RecognizedTranscriptWord(
                index,
                region.StartSeconds + (step * index),
                index == words.Length - 1 ? region.EndSeconds : region.StartSeconds + (step * (index + 1)),
                word,
                confidence))
            .ToArray();
    }

    private sealed class ThrowingDiarizationEngine : ISpeakerDiarizationEngine
    {
        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(string normalizedAudioPath, double durationSeconds, IReadOnlyList<SpeechRegion> speechRegions, CancellationToken cancellationToken) =>
            throw new FileNotFoundException("SortFormer ONNX export was not found.");
    }

    private sealed class RecordingDiarizationEngine(IReadOnlyList<DiarizedSpeakerTurn> turns)
        : ISpeakerDiarizationEngine
    {
        public RecordingDiarizationEngine()
            : this([new DiarizedSpeakerTurn("spk_0", 0.0, 1.0, Confidence: 0.9, HasOverlap: false)])
        {
        }

        public int CallCount { get; private set; }

        public string? LastAudioPath { get; private set; }

        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
            string normalizedAudioPath,
            double durationSeconds,
            IReadOnlyList<SpeechRegion> speechRegions,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastAudioPath = normalizedAudioPath;
            return Task.FromResult(turns);
        }
    }

    private sealed class SequencedDiarizationEngine(params IReadOnlyList<DiarizedSpeakerTurn>[] turnsByCall)
        : ISpeakerDiarizationEngine
    {
        private readonly Queue<IReadOnlyList<DiarizedSpeakerTurn>> turnsByCall = new(turnsByCall);

        public int CallCount { get; private set; }

        public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
            string normalizedAudioPath,
            double durationSeconds,
            IReadOnlyList<SpeechRegion> speechRegions,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(turnsByCall.Count == 0 ? [] : turnsByCall.Dequeue());
        }
    }

    private sealed class RecordingModelDownloader : IModelDownloaderContract
    {
        public string? ModelId { get; private set; }

        public string? FileName { get; private set; }

        public string? DestinationPath { get; private set; }

        public Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null)
        {
            ModelId = modelId;
            FileName = fileName;
            DestinationPath = destinationPath;
            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(destinationPath, SortFormerTestFixtures.ModelBytes);
            return Task.FromResult(true);
        }

        public Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DestinationPath = destinationPath;
            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(destinationPath, [4, 3, 2, 1]);
            return Task.FromResult(true);
        }

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class FakeEnhancementStage : Trackdub.Application.Transcripts.Pipeline.ITranscriptGenerationStage
    {
        public string StageName => Trackdub.Domain.StageRuns.StageNames.SpeechEnhancement;

        public Task<Trackdub.Application.Transcripts.Pipeline.TranscriptGenerationContext> ExecuteAsync(
            Trackdub.Application.Transcripts.Pipeline.TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<Trackdub.Contracts.Pipeline.PipelineProgressEvent>? progress = null) =>
            Task.FromResult(context);
    }
}
