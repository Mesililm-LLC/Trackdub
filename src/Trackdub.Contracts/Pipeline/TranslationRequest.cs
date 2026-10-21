namespace Trackdub.Contracts.Pipeline;

public sealed record TranslationRequest(
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<TranslationInputSegment> Segments,
    string? PreferredModelAlias = null,
    string? ResolvedModelEntryPath = null,
    IReadOnlyList<TranslationGlossaryHint>? GlossaryHints = null,
    string? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null,
    Trackdub.Domain.Translation.TranslationModelProvenance? ModelProvenance = null);

public sealed record TranslationGlossaryHint(
    string SourceTerm,
    string TargetTerm,
    bool IsCaseSensitive,
    IReadOnlyList<TranslationGlossarySourceMatch>? SourceMatches = null);

public sealed record TranslationGlossarySourceMatch(
    int SegmentIndex,
    int StartTextElementIndex,
    int TextElementLength,
    string MatchedSourceTerm);

public sealed record TranslationInputSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Text);

public sealed record TranslatedTextSegment(
    int Index,
    double StartSeconds,
    double EndSeconds,
    string Text);
