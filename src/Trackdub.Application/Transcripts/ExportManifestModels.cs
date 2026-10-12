using Trackdub.Contracts;
using Trackdub.Domain;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed record ExportManifest(
    Guid ProjectId,
    DateTimeOffset CreatedAtUtc,
    Guid? ExportStageRunId,
    string? SourceLanguage,
    string? TargetLanguage,
    ExportOutputContainer? Container,
    ExportManifestLoudness? Loudness,
    IReadOnlyList<Guid> StageRunIds,
    IReadOnlyList<string> ModelIds,
    IReadOnlyList<string> TtsVoices,
    IReadOnlyList<ExportManifestOutput> Outputs,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<ExportManifestSegment> Segments,
    ExportManifestGating? Gating = null,
    bool HasTimelineMedia = false);

/// <summary>
/// The export-gating flag values for the run that produced this manifest, captured so the
/// resume evaluator can detect audio/subtitle/encoder/container changes and force a rerun.
/// Values are produced by <c>ExportResumeGating</c>, the same helper the execution snapshot
/// uses, so capture and comparison share one normalization. Nullable on the manifest so older
/// projects (whose manifests predate this field) deserialize with <c>Gating == null</c>.
/// </summary>
public sealed record ExportManifestGating(
    IReadOnlyDictionary<string, string> Flags);

public sealed record ExportManifestLoudness(
    double TargetLufs,
    double? AchievedLufs);

public sealed record ExportManifestOutput(
    string Kind,
    string Path,
    string PathBase = ExportManifestOutputPathBases.Delivery);

public static class ExportManifestOutputPathBases
{
    public const string Delivery = "delivery";
    public const string Artifact = "artifact";
}

public sealed record ExportManifestSegment(
    int SegmentIndex,
    Guid? TtsTakeId,
    bool UsedVoiceCloning,
    Guid? ReferenceClipArtifactId,
    string? ModelId = null,
    string? VoiceId = null,
    Guid? StageRunId = null);

public sealed record ExportManifestBuildRequest(
    Guid ProjectId,
    IReadOnlyList<TranslatedSegment> TranslatedSegments,
    IReadOnlyList<TtsTake> TtsTakes,
    IReadOnlyList<StageRunRecord> StageRuns,
    Guid? ExportStageRunId = null,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    ExportOutputContainer? Container = null,
    double? TargetLufs = null,
    double? AchievedLufs = null,
    IReadOnlyList<ExportManifestOutput>? Outputs = null,
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyCollection<int>? RenderedSegmentIndices = null,
    ExportManifestGating? Gating = null,
    bool HasTimelineMedia = false);

public static class ExportManifestBuilder
{
    public static ExportManifest Build(
        Guid projectId,
        IReadOnlyList<TranslatedSegment> translatedSegments,
        IReadOnlyList<TtsTake> ttsTakes) =>
        Build(new ExportManifestBuildRequest(
            projectId,
            translatedSegments,
            ttsTakes,
            StageRuns: []));

    public static ExportManifest Build(ExportManifestBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.TranslatedSegments);
        ArgumentNullException.ThrowIfNull(request.TtsTakes);
        if (request.ProjectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(request));
        }

        Dictionary<int, TtsTake> latestTakesBySegmentIndex = request.TtsTakes
            .GroupBy(take => take.SegmentIndex)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(take => take.CreatedAtUtc).First());
        HashSet<int>? renderedSegmentIndices = request.RenderedSegmentIndices?.ToHashSet();

        var selectedTakes = new List<TtsTake>();
        ExportManifestSegment[] segments = request.TranslatedSegments
            .Where(segment => renderedSegmentIndices is null || renderedSegmentIndices.Contains(segment.SegmentIndex))
            .OrderBy(segment => segment.SegmentIndex)
            .Select(segment =>
            {
                latestTakesBySegmentIndex.TryGetValue(segment.SegmentIndex, out TtsTake? take);
                if (take is not null)
                {
                    selectedTakes.Add(take);
                }

                if (take?.Kind is TtsTakeKind.VoiceCloned && take.ReferenceClipArtifactId is null)
                {
                    throw new InvalidOperationException(
                        $"Export cannot continue because cloned TTS take '{take.Id:D}' is missing its reference clip artifact id.");
                }

                return new ExportManifestSegment(
                    segment.SegmentIndex,
                    take?.Id,
                    take?.Kind is TtsTakeKind.VoiceCloned,
                    take?.Kind is TtsTakeKind.VoiceCloned ? take.ReferenceClipArtifactId : null,
                    take?.ModelId,
                    take?.VoiceId,
                    take?.StageRunId);
            })
            .ToArray();

        Guid[] stageRunIds = request.StageRuns
            .Select(static stageRun => stageRun.Id)
            .Concat(request.ExportStageRunId is Guid exportStageRunId ? [exportStageRunId] : [])
            .Where(static id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        string[] modelIds = selectedTakes
            .Select(static take => take.ModelId)
            .Where(static modelId => !string.IsNullOrWhiteSpace(modelId))
            .Select(static modelId => modelId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static modelId => modelId, StringComparer.Ordinal)
            .ToArray();

        string[] voices = selectedTakes
            .Select(static take => take.VoiceId)
            .Where(static voiceId => !string.IsNullOrWhiteSpace(voiceId))
            .Select(static voiceId => voiceId!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static voiceId => voiceId, StringComparer.Ordinal)
            .ToArray();

        ExportManifestLoudness? loudness = request.TargetLufs is double targetLufs
            ? new ExportManifestLoudness(targetLufs, request.AchievedLufs)
            : null;

        return new ExportManifest(
            request.ProjectId,
            DateTimeOffset.UtcNow,
            request.ExportStageRunId,
            NormalizeLanguage(request.SourceLanguage),
            NormalizeLanguage(request.TargetLanguage),
            request.Container,
            loudness,
            stageRunIds,
            modelIds,
            voices,
            request.Outputs ?? [],
            request.Warnings ?? [],
            segments,
            request.Gating,
            request.HasTimelineMedia);
    }

    private static string? NormalizeLanguage(string? language) =>
        string.IsNullOrWhiteSpace(language)
            ? null
            : language.Trim().ToLowerInvariant();
}
