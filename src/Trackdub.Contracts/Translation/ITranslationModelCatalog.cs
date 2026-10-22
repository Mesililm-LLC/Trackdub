using System.Collections.Immutable;

namespace Trackdub.Contracts.Translation;

public interface ITranslationModelCatalog
{
    TranslationModelCatalogSnapshot Snapshot { get; }
    Task<TranslationModelCatalogSnapshot> RefreshAsync(bool offline, CancellationToken cancellationToken);
}

public sealed record TranslationModelCatalogSnapshot(
    DateTimeOffset FetchedAtUtc,
    string PolicyVersion,
    ImmutableArray<TranslationModelDescriptor> Models,
    ImmutableArray<TranslationModelExclusion> Exclusions)
{
    public static TranslationModelCatalogSnapshot Empty { get; } = new(
        DateTimeOffset.MinValue, "opus-mt-publishers-v1", [], []);
}

public sealed record TranslationModelDescriptor(
    string ModelId,
    string Repository,
    string Revision,
    string SourceLanguage,
    string TargetLanguage,
    string? TargetPrefix,
    bool IsMultilingual,
    string License,
    string LicenseEvidence,
    ImmutableArray<TranslationModelFile> Files);

public sealed record TranslationModelFile(string Path, long SizeBytes, string Sha256);
public sealed record TranslationModelExclusion(string Repository, string Reason);
