using Trackdub.Application.Transcripts;
using Trackdub.Application.Projects;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// Drives the export resume decision (StageArtifactResumeEvaluator.CanResumeStageAsync) end to
/// end: a completed export run + present output + a persisted ExportManifest whose gating flags
/// are compared against the current run's execution snapshot. These tests fail if the wiring that
/// reads the persisted gating flags is reverted, unlike the snapshot-shape tests that only assert
/// the captured dictionary.
/// </summary>
public sealed class ExportResumeGatingTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Timeline_media_forces_render_when_present_or_removed_since_export(bool currentlyPlaced)
    {
        using var temp = new TempDir();
        var fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating(), hasTimelineMedia: !currentlyPlaced);
        var state = fixture.State with
        {
            ProjectUiSettings = new ProjectUiSettings(TimelinePlacements: currentlyPlaced
                ? [new ProjectTimelineMediaPlacement("extra.wav", "Extra", 1, 1d, 1d)] : [])
        };
        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(state, fixture.ArtifactStore,
            StageNames.Export, BaselineSnapshot(), temp.Path, exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_true_when_gating_flags_match()
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Theory]
    [InlineData(ExportResumeGating.ApplyTimbrePolishKey, "False")]
    [InlineData(ExportResumeGating.RestoreOriginalPanKey, "True")]
    [InlineData(ExportResumeGating.MatchOriginalLoudnessKey, "True")]
    [InlineData(ExportResumeGating.BurnInSubtitlesKey, "True")]
    [InlineData(ExportResumeGating.VideoEncoderKey, "nvenc")]
    [InlineData(ExportResumeGating.SubtitleSourceKey, "Transcript")]
    [InlineData(ExportResumeGating.SubtitleFormatsKey, "Vtt")]
    [InlineData(ExportResumeGating.ExportFormatKey, "mkv")]
    [InlineData(ExportResumeGating.TargetLufsKey, "-23")]
    [InlineData(ExportResumeGating.SourceGainDbKey, "-3")]
    [InlineData(ExportResumeGating.DubbedSpeechGainDbKey, "2")]
    [InlineData(ExportResumeGating.DuckingGainDbKey, "-6")]
    public async Task CanResumeStageAsync_returns_false_when_a_gating_flag_changes(string key, string changedValue)
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        Dictionary<string, string> snapshot = BaselineSnapshot();
        snapshot[key] = changedValue;

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_true_for_equivalent_subtitle_source_and_formats()
    {
        using var temp = new TempDir();
        // Prior run recorded the raw translated source and the raw ["srt"] format.
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        // Current snapshot built from the raw options: SubtitleSource null (== "translated")
        // and SubtitleFormats ["SRT"] (case-insensitively == the baseline "Srt" token).
        Dictionary<string, string> snapshot = new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            sourceAudioKind: ArtifactKind.NormalizedAudio,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportResumeGating_ResolveSubtitleSource(null),
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(["SRT"]),
            videoEncoder: VideoEncoderPreference.Auto,
            targetLufs: ExportLoudnessTargets.OnlineLufs,
            sourceGainDb: 0d,
            dubbedSpeechGainDb: 0d,
            duckingGainDb: null), StringComparer.OrdinalIgnoreCase);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_true_for_default_subtitle_formats()
    {
        using var temp = new TempDir();
        // Prior default run: raw options.SubtitleFormats == null, persisted as the "default" token.
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, DefaultSubtitleGating());

        // Current run with the same default (null) options. Both sides use
        // SubtitleFormatsTokenFromRawOptions, so null -> "default" on both and nothing changed.
        // This pins the v2 fix: without it the manifest would hold the resolved "Srt" token while
        // the snapshot holds "default", forcing a spurious rerun of every default-subtitle export.
        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            DefaultSubtitleSnapshot(),
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_false_when_default_differs_from_explicit_formats()
    {
        using var temp = new TempDir();
        // Prior default run (null options -> "default" token).
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, DefaultSubtitleGating());

        // Current run explicitly requests ["vtt"] -> a genuinely different export output.
        Dictionary<string, string> snapshot = DefaultSubtitleSnapshot();
        snapshot[ExportResumeGating.SubtitleFormatsKey] =
            ExportResumeGating.SubtitleFormatsTokenFromRawOptions(["vtt"]);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_false_when_default_differs_from_empty_formats()
    {
        using var temp = new TempDir();
        // Prior default run (null options -> "default" token).
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, DefaultSubtitleGating());

        // Current run explicitly suppresses all subtitles ([] -> "" token), distinct from default.
        Dictionary<string, string> snapshot = DefaultSubtitleSnapshot();
        snapshot[ExportResumeGating.SubtitleFormatsKey] =
            ExportResumeGating.SubtitleFormatsTokenFromRawOptions([]);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_falls_back_to_resume_when_manifest_has_no_gating()
    {
        using var temp = new TempDir();
        // Older project: manifest predates the gating field (Gating == null).
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, gating: null);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_false_when_export_output_missing()
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(
            temp.Path,
            BaselineGating(),
            writeExportOutput: false);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_resumes_against_an_explicit_absolute_output_path()
    {
        using var temp = new TempDir();
        // The prior run delivered to a custom absolute path outside the default exports/ dir;
        // the engine passes that effective path, and resume must check it verbatim.
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());
        string customOutput = Path.Join(temp.Path, "delivery", "final.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(customOutput)!);
        await File.WriteAllBytesAsync(customOutput, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportPath: customOutput,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_returns_false_when_the_requested_output_path_differs()
    {
        using var temp = new TempDir();
        // Default-path export exists on disk, but the current run targets a different custom
        // path: the cached artifact must not satisfy it.
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());
        string otherOutput = Path.Join(temp.Path, "elsewhere", "other.mp4");

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            BaselineSnapshot(),
            temp.Path,
            exportPath: otherOutput,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    [Fact]
    public async Task CanResumeStageAsync_distinguishes_unset_ducking_from_explicit_zero()
    {
        using var temp = new TempDir();
        ExportResumeFixture fixture = await ExportResumeFixture.CreateAsync(temp.Path, BaselineGating());

        // An explicit 0 dB ducking gain produces a different mix than no ducking; the tokens
        // must differ ("" for unset vs "0" for explicit) so the cached export is not reused.
        Dictionary<string, string> snapshot = BaselineSnapshot();
        snapshot[ExportResumeGating.DuckingGainDbKey] = "0";

        bool canResume = await StageArtifactResumeEvaluator.CanResumeStageAsync(
            fixture.State,
            fixture.ArtifactStore,
            StageNames.Export,
            snapshot,
            temp.Path,
            exportPath: fixture.ExportRelativePath,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(canResume);
    }

    private static ExportSubtitleSource ExportResumeGating_ResolveSubtitleSource(string? source) =>
        source?.Trim().ToLowerInvariant() switch
        {
            "transcript" => ExportSubtitleSource.Transcript,
            "bilingual" => ExportSubtitleSource.Bilingual,
            _ => ExportSubtitleSource.Translated,
        };

    private static ExportManifestGating BaselineGating() =>
        // The manifest now persists the RAW requested formats (["srt"]), the same producer the
        // snapshot uses, so the explicit-request baseline matches an equivalent current snapshot.
        new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            sourceAudioKind: ArtifactKind.NormalizedAudio,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportSubtitleSource.Translated,
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(["srt"]),
            videoEncoder: VideoEncoderPreference.Auto,
            targetLufs: ExportLoudnessTargets.OnlineLufs,
            sourceGainDb: 0d,
            dubbedSpeechGainDb: 0d,
            duckingGainDb: null));

    // The DEFAULT path: a prior run whose raw options were null (no subtitle formats specified).
    // The manifest records the "default" token, so a later default run's snapshot must match and
    // resume. This is the case the v2 review flagged as spuriously rerunning.
    private static ExportManifestGating DefaultSubtitleGating() =>
        new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            sourceAudioKind: ArtifactKind.NormalizedAudio,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportSubtitleSource.Translated,
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(null),
            videoEncoder: VideoEncoderPreference.Auto,
            targetLufs: ExportLoudnessTargets.OnlineLufs,
            sourceGainDb: 0d,
            dubbedSpeechGainDb: 0d,
            duckingGainDb: null));

    private static Dictionary<string, string> DefaultSubtitleSnapshot() =>
        new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            sourceAudioKind: ArtifactKind.NormalizedAudio,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportSubtitleSource.Translated,
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(null),
            videoEncoder: VideoEncoderPreference.Auto,
            targetLufs: ExportLoudnessTargets.OnlineLufs,
            sourceGainDb: 0d,
            dubbedSpeechGainDb: 0d,
            duckingGainDb: null), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> BaselineSnapshot() =>
        new(ExportResumeGating.Build(
            ExportOutputContainer.Mp4,
            sourceAudioKind: ArtifactKind.NormalizedAudio,
            applyTimbrePolish: true,
            restoreOriginalPan: false,
            matchOriginalLoudness: false,
            burnInSubtitles: false,
            subtitleSource: ExportSubtitleSource.Translated,
            subtitleFormatsToken: ExportResumeGating.SubtitleFormatsTokenFromRawOptions(["srt"]),
            videoEncoder: VideoEncoderPreference.Auto,
            targetLufs: ExportLoudnessTargets.OnlineLufs,
            sourceGainDb: 0d,
            dubbedSpeechGainDb: 0d,
            duckingGainDb: null), StringComparer.OrdinalIgnoreCase);

    private sealed record ExportResumeFixture(
        TranscriptProjectState State,
        FakeArtifactStore ArtifactStore,
        string ExportRelativePath)
    {
        public static async Task<ExportResumeFixture> CreateAsync(
            string projectRootPath,
            ExportManifestGating? gating,
            bool writeExportOutput = true,
            bool hasTimelineMedia = false)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Guid projectId = Guid.NewGuid();
            Guid mediaAssetId = Guid.NewGuid();
            Guid transcriptRevisionId = Guid.NewGuid();
            var project = new TrackdubProject(projectId, "Export", now, now);

            string sourcePath = Path.Join(projectRootPath, "source.mp4");
            Directory.CreateDirectory(projectRootPath);
            await File.WriteAllBytesAsync(sourcePath, [0, 0, 0, 24]);

            var mediaAsset = new MediaAsset(
                mediaAssetId,
                projectId,
                sourcePath,
                Path.GetFileName(sourcePath),
                "source-hash",
                100,
                now,
                "mp4",
                6.0d,
                HasAudio: true,
                HasVideo: true,
                now);

            TranscriptSegment transcriptSegment = TranscriptSegment.Create(
                transcriptRevisionId,
                0,
                1.0d,
                2.0d,
                "Hello",
                Guid.NewGuid(),
                "en");

            StageRunRecord exportRun = StageRunRecord
                .Start(projectId, StageNames.Export, now)
                .Complete(now.AddSeconds(1));

            var artifactStore = new FakeArtifactStore(projectRootPath);

            var manifest = new ExportManifest(
                projectId,
                now,
                exportRun.Id,
                SourceLanguage: "en",
                TargetLanguage: "es",
                Container: ExportOutputContainer.Mp4,
                Loudness: null,
                StageRunIds: [exportRun.Id],
                ModelIds: [],
                TtsVoices: [],
                Outputs: [],
                Warnings: [],
                Segments: [],
                Gating: gating,
                HasTimelineMedia: hasTimelineMedia);
            await artifactStore.WriteJsonAsync(
                ProjectArtifactPaths.GetExportManifestRelativePath(exportRun.Id),
                manifest,
                CancellationToken.None);

            string exportRelativePath = "exports/dubbed.mp4";
            if (writeExportOutput)
            {
                string exportDir = "exports".TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string exportFile = "dubbed.mp4".TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string exportFullPath = Path.Join(projectRootPath, exportDir, exportFile);
                Directory.CreateDirectory(Path.GetDirectoryName(exportFullPath)!);
                await File.WriteAllBytesAsync(exportFullPath, [1, 2, 3, 4]);
            }

            var openResult = new OpenProjectResult(
                project,
                mediaAsset,
                new SourceMediaReference(
                    mediaAsset.SourceFilePath,
                    mediaAsset.SourceFileName,
                    new FileFingerprint(mediaAsset.FingerprintSha256, mediaAsset.SourceSizeBytes, mediaAsset.SourceLastWriteTimeUtc),
                    ProbeSnapshot(mediaAsset.DurationSeconds),
                    DateTimeOffset.UtcNow),
                SourceMediaStatus.Available,
                SourceStatusMessage: null,
                Artifacts: [],
                TranscriptLanguage: "en");
            TranscriptRevision transcriptRevision = TranscriptRevision.Create(
                projectId,
                stageRunId: null,
                revisionNumber: 1,
                DateTimeOffset.UtcNow);
            TranslationRevision translationRevision = TranslationRevision.Create(
                projectId,
                stageRunId: null,
                transcriptRevision.Id,
                "es",
                revisionNumber: 1,
                DateTimeOffset.UtcNow);

            var state = new TranscriptProjectState(
                openResult,
                transcriptRevision,
                [transcriptSegment],
                Speakers: [],
                SpeakerTurns: [],
                translationRevision,
                TranslatedSegments: [],
                IsTranslationStale: false,
                TranscriptLanguage: "en",
                StageRuns: [exportRun],
                SupportedTargetLanguages: [],
                SelectedTranslationTargetLanguage: "es",
                StaleTranslatedSegmentIndices: new HashSet<int>(),
                WaveformSummary: null,
                AvailableVoices: [],
                VoiceAssignments: [],
                TtsTakes: [],
                TtsSegmentStates: [],
                VoiceAssignmentWarnings: []);

            return new ExportResumeFixture(state, artifactStore, exportRelativePath);
        }

        private static MediaProbeSnapshot ProbeSnapshot(double durationSeconds) =>
            new(
                "mp4",
                "MP4",
                durationSeconds,
                BitRate: null,
                AudioStreams: [new MediaAudioStream(0, "aac", Channels: 2, SampleRate: 48000, durationSeconds)],
                VideoStreams: [new MediaVideoStream(1, "h264", Width: 1920, Height: 1080, FrameRate: 24.0d, durationSeconds)],
                SubtitleStreams: []);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Join(System.IO.Path.GetTempPath(), $"trackdub-export-resume-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to delete temp directory '{Path}': {ex}");
            }
            catch (UnauthorizedAccessException ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to delete temp directory '{Path}' due to access restrictions: {ex}");
            }
        }
    }
}
