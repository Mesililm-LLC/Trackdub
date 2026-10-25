using Trackdub.Application.Mixing;
using Trackdub.Domain.Mixing;

namespace Trackdub.Application.LipSynthesis;

/// <summary>
/// Builds the audio that drives video lip synthesis: the dubbed speech takes alone, at their
/// timeline positions. Mouth-driving models follow the voice, and the exported mix also carries
/// the original ambiance, music and other timeline media.
/// </summary>
internal sealed class DubbedSpeechDriverRenderer(
    MixPlanStore mixPlanStore,
    IPreviewRangeRenderer renderer,
    IArtifactStore artifactStore)
{
    internal const string RelativePath = "artifacts/lip-synthesis/driver/dubbed-speech.wav";

    // Renders the original-audio bed at an inaudible level so only the dubbed takes remain.
    internal const double MutedSourceGainDb = -200d;

    /// <summary>
    /// Renders the saved mix plan with the original bed and extra media clips muted and timbre
    /// polish off. Returns null, so the caller keeps the full dubbed mix, when there is no mix plan
    /// with audible speech or rendering fails.
    /// </summary>
    public async Task<string?> TryRenderAsync(double? mediaDurationSeconds, CancellationToken cancellationToken)
    {
        try
        {
            MixPlan? mixPlan = await mixPlanStore.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (mixPlan is null || !mixPlan.SpeechClips.Any(static clip => !clip.IsSilentGap))
            {
                return null;
            }

            double endSeconds = Math.Max(
                mediaDurationSeconds is double duration && double.IsFinite(duration) ? duration : 0d,
                mixPlan.SpeechClips.Max(static clip => clip.EndSeconds));
            if (endSeconds <= 0d)
            {
                return null;
            }

            string outputPath = artifactStore.GetPath(RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            PreviewRangeRenderResult rendered = await renderer.RenderAsync(
                new PreviewRangeRenderRequest(
                    mixPlan with { SourceGainDb = MutedSourceGainDb, ApplyTimbrePolish = false, MediaClips = [] },
                    StartSeconds: 0d,
                    EndSeconds: endSeconds,
                    OutputPath: outputPath),
                cancellationToken).ConfigureAwait(false);
            return File.Exists(rendered.OutputPath) ? rendered.OutputPath : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException
                                       or ArgumentException or System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
