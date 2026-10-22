using Trackdub.Domain.Translation;

namespace Trackdub.Contracts.Pipeline;

public enum TranslationRoutingKind
{
    Direct = 1,
    Pivot = 2,
    Unavailable = 3
}

public enum TranslationPreparationStatus { Unavailable, Ready, DownloadRequired, SourceUnresolved }

public sealed record TranslationTargetLanguageOption(
    string LanguageCode,
    string DisplayName,
    TranslationRoutingKind RoutingKind,
    bool IsAvailable,
    string Detail,
    TranslationPreparationStatus PreparationStatus = TranslationPreparationStatus.Unavailable,
    long? ExpectedDownloadBytes = null)
{
    /// <summary>Language name only; routing <see cref="Detail"/> is for logs and pipeline, not UI labels.</summary>
    public string DisplayLabel => DisplayName;
    public TranslationPreparationStatus Status => IsAvailable ? TranslationPreparationStatus.Ready : PreparationStatus;
    public bool IsSelectable => IsAvailable || Status is TranslationPreparationStatus.DownloadRequired or TranslationPreparationStatus.SourceUnresolved;
}

public sealed record TranslationRouteSelection(
    string SourceLanguage,
    string TargetLanguage,
    TranslationRoutingKind RoutingKind,
    bool IsAvailable,
    string ProviderName,
    string RouteDetail,
    string? ModelId = null,
    string? PreferredModelAlias = null,
    string? ResolvedModelEntryPath = null,
    string? UnavailableReason = null,
    string? EngineFamily = null,
    TranslationPreparationStatus PreparationStatus = TranslationPreparationStatus.Unavailable,
    long? ExpectedDownloadBytes = null,
    TranslationModelProvenance? ModelProvenance = null)
{
    public TranslationPreparationStatus Status => IsAvailable ? TranslationPreparationStatus.Ready : PreparationStatus;
    public bool IsSelectable => IsAvailable || Status == TranslationPreparationStatus.DownloadRequired;
}

public sealed record TranslationExecutionMetadata(
    string ProviderName,
    string? ModelId,
    string? ModelAlias,
    string? SelectedExecutionProvider,
    TranslationRoutingKind RoutingKind,
    TranslationModelProvenance? ModelProvenance = null);

public interface ITranslationLanguageRouter
{
    Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
        string sourceLanguage,
        CancellationToken cancellationToken);

    Task<TranslationRouteSelection> ResolveRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        string? preferredModelAlias = null);
}

public interface ITranslationExecutionMetadataReporter
{
    TranslationExecutionMetadata? LastExecutionMetadata { get; }
}
