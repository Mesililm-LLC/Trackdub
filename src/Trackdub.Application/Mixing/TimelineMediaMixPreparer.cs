using System.Globalization;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Mixing;

namespace Trackdub.Application.Mixing;

/// <summary>Materializes placed audio/video as project-owned PCM for both preview and export.</summary>
public sealed class TimelineMediaMixPreparer(
    IArtifactStore artifactStore,
    IAudioExtractionService audioExtractionService,
    IFileFingerprintService fingerprintService,
    IMediaProbe mediaProbe)
{
    private const double TimingToleranceSeconds = 0.01d;

    public static async Task<IReadOnlyList<MixMediaClip>> PrepareAsync(
        TimelineMediaMixPreparer? preparer,
        TranscriptProjectState state,
        CancellationToken cancellationToken)
    {
        MediaAsset media = TranscriptWorkflowUtilities.GetRequiredMediaAsset(state);
        IReadOnlyList<ProjectTimelineMediaPlacement> placements = GetPlacements(state);
        if (placements.Count == 0) return [];
        if (preparer is null) throw new InvalidOperationException("Timeline media audio preparation is unavailable.");

        double mixBedEndSeconds = ResolveMixBedEndSeconds(state, media);
        var clips = new List<MixMediaClip>();
        foreach (ProjectTimelineMediaPlacement placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(placement.StartSeconds) || placement.StartSeconds < 0d ||
                !double.IsFinite(placement.DurationSeconds) || placement.DurationSeconds <= 0d ||
                placement.StartSeconds + placement.DurationSeconds > mixBedEndSeconds + TimingToleranceSeconds)
            {
                throw new InvalidOperationException(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Timeline clip '{placement.DisplayName}' must fit within {mixBedEndSeconds:0.###} s of source audio."));
            }

            if (!File.Exists(placement.MediaPath))
            {
                throw new FileNotFoundException(
                    $"Timeline media '{placement.DisplayName}' is missing. Restore the file before mixing.",
                    placement.MediaPath);
            }

            MediaProbeSnapshot probe = await preparer.mediaProbe
                .ProbeAsync(placement.MediaPath, cancellationToken)
                .ConfigureAwait(false);
            if (probe.AudioStreams.Count == 0)
                continue;

            double probedAudioSeconds = probe.AudioStreams[0].DurationSeconds;
            if (double.IsFinite(probedAudioSeconds) &&
                probedAudioSeconds > 0d &&
                placement.DurationSeconds > probedAudioSeconds + TimingToleranceSeconds)
            {
                throw AudioShorterThanSlot(placement, probedAudioSeconds);
            }

            FileFingerprint fingerprint = await preparer.fingerprintService
                .ComputeAsync(placement.MediaPath, cancellationToken)
                .ConfigureAwait(false);
            string relativePath = $"artifacts/timeline-media/{fingerprint.Sha256.ToLowerInvariant()}.wav";
            double preparedSeconds;
            if (!File.Exists(preparer.artifactStore.GetPath(relativePath)))
            {
                await using ArtifactWriteHandle handle = preparer.artifactStore.CreateWriteHandle(relativePath);
                AudioExtractionResult extracted = await preparer.audioExtractionService
                    .ExtractNormalizedAudioAsync(placement.MediaPath, handle.TemporaryPath, cancellationToken)
                    .ConfigureAwait(false);
                await preparer.artifactStore.CommitAsync(handle, cancellationToken).ConfigureAwait(false);
                preparedSeconds = extracted.DurationSeconds;
            }
            else
            {
                MediaProbeSnapshot prepared = await preparer.mediaProbe
                    .ProbeAsync(preparer.artifactStore.GetPath(relativePath), cancellationToken)
                    .ConfigureAwait(false);
                preparedSeconds = prepared.AudioStreams.Count == 0
                    ? prepared.DurationSeconds
                    : prepared.AudioStreams[0].DurationSeconds;
            }

            if (!double.IsFinite(preparedSeconds) ||
                placement.DurationSeconds > preparedSeconds + TimingToleranceSeconds)
            {
                throw AudioShorterThanSlot(placement, preparedSeconds);
            }

            clips.Add(new MixMediaClip(relativePath, placement.StartSeconds, placement.DurationSeconds));
        }

        return clips;
    }

    /// <summary>
    /// Drops the primary source lane. It already supplies the mix bed.
    /// Marked placements win. Otherwise the current source file path wins, including after a move.
    /// Older saves that only parked the source at track zero, time zero keep that position fallback.
    /// </summary>
    public static IReadOnlyList<ProjectTimelineMediaPlacement> GetPlacements(TranscriptProjectState state)
    {
        IReadOnlyList<ProjectTimelineMediaPlacement> placements = state.ProjectUiSettings?.TimelinePlacements ?? [];
        if (placements.Count == 0)
            return [];

        if (placements.Any(static placement => placement.IsPrimarySource))
            return placements.Where(static placement => !placement.IsPrimarySource).ToArray();

        MediaAsset media = TranscriptWorkflowUtilities.GetRequiredMediaAsset(state);
        if (placements.Any(placement => IsSameMediaPath(placement.MediaPath, media.SourceFilePath)))
            return placements.Where(placement => !IsSameMediaPath(placement.MediaPath, media.SourceFilePath)).ToArray();

        return placements
            .Where(static placement =>
                placement.TrackIndex != 0 || Math.Abs(placement.StartSeconds) > TimingToleranceSeconds)
            .ToArray();
    }

    private static double ResolveMixBedEndSeconds(TranscriptProjectState state, MediaAsset media)
    {
        double? sourceAudioEndSeconds = null;
        ProjectArtifact? sourceArtifact = TranscriptWorkflowUtilities.GetLatestAcceptedAmbianceStem(state.ProjectState.Artifacts)
            ?? state.ProjectState.Artifacts
                .Where(static artifact => artifact.Kind == ArtifactKind.NormalizedAudio)
                .OrderByDescending(static artifact => artifact.CreatedAtUtc)
                .FirstOrDefault();
        if (sourceArtifact?.DurationSeconds is double sourceDuration &&
            double.IsFinite(sourceDuration) &&
            sourceDuration > 0d)
        {
            sourceAudioEndSeconds = sourceDuration;
        }

        if (double.IsFinite(media.DurationSeconds) && media.DurationSeconds > 0d)
        {
            sourceAudioEndSeconds = sourceAudioEndSeconds is double sourceEnd
                ? Math.Min(sourceEnd, media.DurationSeconds)
                : media.DurationSeconds;
        }

        return sourceAudioEndSeconds ?? 0d;
    }

    private static bool IsSameMediaPath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return FilePathComparison.AreSame(left, right);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static InvalidOperationException AudioShorterThanSlot(
        ProjectTimelineMediaPlacement placement,
        double audioSeconds) =>
        new(string.Create(
            CultureInfo.InvariantCulture,
            $"Timeline clip '{placement.DisplayName}' is longer than its audio ({audioSeconds:0.###} s)."));

    private readonly IArtifactStore artifactStore = artifactStore;
    private readonly IAudioExtractionService audioExtractionService = audioExtractionService;
    private readonly IFileFingerprintService fingerprintService = fingerprintService;
    private readonly IMediaProbe mediaProbe = mediaProbe;
}
