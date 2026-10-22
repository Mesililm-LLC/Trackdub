namespace Trackdub.Domain.Translation;

/// <summary>Exact immutable model identity captured when a translation route is selected.</summary>
public sealed record TranslationModelProvenance(
    string Repository,
    string Revision,
    string Sha256,
    IReadOnlyDictionary<string, string> FileHashes,
    string? TargetPrefix,
    string? CatalogPolicyVersion);
