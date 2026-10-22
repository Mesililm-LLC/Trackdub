namespace Trackdub.Domain.Translation;

public sealed record TranslationRevision(
    Guid Id,
    Guid ProjectId,
    Guid? StageRunId,
    Guid SourceTranscriptRevisionId,
    string TargetLanguage,
    string? TranslationProvider,
    string? ModelId,
    string? ExecutionProvider,
    int RevisionNumber,
    DateTimeOffset CreatedAtUtc,
    TranslationModelProvenance? ModelProvenance = null)
{
    public static TranslationRevision Create(
        Guid projectId,
        Guid? stageRunId,
        Guid sourceTranscriptRevisionId,
        string targetLanguage,
        int revisionNumber,
        DateTimeOffset createdAtUtc,
        string? translationProvider = null,
        string? modelId = null,
        string? executionProvider = null,
        TranslationModelProvenance? modelProvenance = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (stageRunId is { } runId && runId == Guid.Empty)
        {
            throw new ArgumentException("Stage run id must be null or a non-empty GUID.", nameof(stageRunId));
        }

        if (sourceTranscriptRevisionId == Guid.Empty)
        {
            throw new ArgumentException("Source transcript revision id is required.", nameof(sourceTranscriptRevisionId));
        }

        if (revisionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(revisionNumber), "Revision number must be positive.");
        }

        return new TranslationRevision(
            Guid.NewGuid(),
            projectId,
            stageRunId,
            sourceTranscriptRevisionId,
            NormalizeLanguageCode(targetLanguage, nameof(targetLanguage)),
            NormalizeOptional(translationProvider),
            NormalizeOptional(modelId),
            NormalizeOptional(executionProvider),
            revisionNumber,
            createdAtUtc,
            modelProvenance);
    }

    private static string NormalizeLanguageCode(string? languageCode, string paramName)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            throw new ArgumentException("Language code is required.", paramName);
        }

        return languageCode.Trim().ToLowerInvariant();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
