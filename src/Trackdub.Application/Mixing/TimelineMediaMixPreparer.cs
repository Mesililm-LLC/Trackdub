using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain.Mixing;

namespace Trackdub.Application.Mixing;

/// <summary>Materializes placed audio/video as project-owned PCM for both preview and export.</summary>
public sealed class TimelineMediaMixPreparer(
    IArtifactStore artifactStore,
    IAudioExtractionService audioExtractionService,
    IFileFingerprintService fingerprintService)
{
    public static async Task<IReadOnlyList<MixMediaClip>> PrepareAsync(
        TimelineMediaMixPreparer? preparer,
        TranscriptProjectState state,
        CancellationToken cancellationToken)
    {
        var media = TranscriptWorkflowUtilities.GetRequiredMediaAsset(state);
        var placements = GetPlacements(state);
        if (placements.Count == 0) return [];
        if (preparer is null) throw new InvalidOperationException("Timeline media audio preparation is unavailable.");

        var clips = new List<MixMediaClip>();
        foreach (ProjectTimelineMediaPlacement placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!double.IsFinite(placement.StartSeconds) || placement.StartSeconds < 0d ||
                !double.IsFinite(placement.DurationSeconds) || placement.DurationSeconds <= 0d ||
                placement.StartSeconds + placement.DurationSeconds > media.DurationSeconds + 0.01d)
                throw new InvalidOperationException($"Timeline clip '{placement.DisplayName}' must fit within the source media duration.");
            if (!File.Exists(placement.MediaPath))
                throw new FileNotFoundException($"Timeline media '{placement.DisplayName}' is missing. Restore the file before mixing.", placement.MediaPath);

            var fingerprint = await preparer.fingerprintService.ComputeAsync(placement.MediaPath, cancellationToken).ConfigureAwait(false);
            string relativePath = $"artifacts/timeline-media/{fingerprint.Sha256.ToLowerInvariant()}.wav";
            if (!File.Exists(preparer.artifactStore.GetPath(relativePath)))
            {
                await using var handle = preparer.artifactStore.CreateWriteHandle(relativePath);
                await preparer.audioExtractionService.ExtractNormalizedAudioAsync(
                    placement.MediaPath, handle.TemporaryPath, cancellationToken).ConfigureAwait(false);
                await preparer.artifactStore.CommitAsync(handle, cancellationToken).ConfigureAwait(false);
            }
            clips.Add(new MixMediaClip(relativePath, placement.StartSeconds, placement.DurationSeconds));
        }
        return clips;
    }

    // The desktop persists its primary source lane at track zero, time zero.
    // That lane already supplies the mix bed, including after source relinking.
    public static IReadOnlyList<ProjectTimelineMediaPlacement> GetPlacements(TranscriptProjectState state) =>
        (state.ProjectUiSettings?.TimelinePlacements ?? [])
            .Where(placement => !(placement.TrackIndex == 0 && placement.StartSeconds == 0d))
            .ToArray();

    private readonly IArtifactStore artifactStore = artifactStore;
    private readonly IAudioExtractionService audioExtractionService = audioExtractionService;
    private readonly IFileFingerprintService fingerprintService = fingerprintService;
}
