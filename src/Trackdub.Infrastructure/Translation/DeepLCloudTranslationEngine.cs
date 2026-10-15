using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Translation;

public sealed class DeepLCloudTranslationEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : ITranslationEngine, ITranslationExecutionMetadataReporter, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "deepl";
    public const string ProviderName = ProviderKey;
    public const string EngineFamilyName = "deepl-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.TextDeepL;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;

    public TranslationExecutionMetadata? LastExecutionMetadata { get; private set; }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureEgressConsentAsync(cancellationToken).ConfigureAwait(false);
        if (request.Segments.Count == 0)
        {
            return [];
        }

        string authKey = await ResolveAuthKeyAsync(cancellationToken).ConfigureAwait(false);
        DeepLTranslateRequest payload = new(
            request.Segments.Select(segment => segment.Text).ToArray(),
            NormalizeTargetLanguageCode(request.TargetLanguage),
            NormalizeSourceLanguageCode(request.SourceLanguage));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ResolveTranslateEndpoint(authKey));
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", authKey);
        httpRequest.Headers.UserAgent.ParseAdd("Trackdub/1.0");
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        string responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"DeepL translation failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {ExtractErrorMessage(responseBody)}");
        }

        DeepLTranslateResponse? translated = JsonSerializer.Deserialize<DeepLTranslateResponse>(responseBody, JsonOptions);
        if (translated?.Translations is null || translated.Translations.Length != request.Segments.Count)
        {
            throw new InvalidOperationException(
                $"DeepL returned {translated?.Translations?.Length ?? 0} translation result(s) for {request.Segments.Count} input segment(s).");
        }

        LastExecutionMetadata = new TranslationExecutionMetadata(
            ProviderName,
            ModelId: null,
            ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
            SelectedExecutionProvider: "cloud",
            TranslationRoutingKind.Direct);
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
            BootstrapDetail: "DeepL Cloud API");

        var results = new List<TranslatedTextSegment>(request.Segments.Count);
        for (int i = 0; i < request.Segments.Count; i++)
        {
            TranslationInputSegment source = request.Segments[i];
            results.Add(new TranslatedTextSegment(
                source.Index,
                source.StartSeconds,
                source.EndSeconds,
                translated.Translations[i].Text));
        }

        return results;
    }

    // DeepL supported target languages as of 2025-05 (v2 API).
    // Review when DeepL adds/removes language support: https://developers.deepl.com/docs/resources/supported-languages
    public static bool IsSupportedTargetLanguage(string targetLanguage)
    {
        string normalized = NormalizeTargetLanguageCode(targetLanguage);
        return normalized is
            "BG" or "CS" or "DA" or "DE" or "EL" or "EN" or "EN-GB" or "EN-US" or
            "ES" or "ET" or "FI" or "FR" or "HU" or "ID" or "IT" or "JA" or "KO" or
            "LT" or "LV" or "NB" or "NL" or "PL" or "PT" or "PT-BR" or "PT-PT" or
            "RO" or "RU" or "SK" or "SL" or "SV" or "TR" or "UK" or "ZH" or "ZH-HANS" or "ZH-HANT";
    }

    public static Uri ResolveTranslateEndpoint(string authKey) =>
        new(authKey.Trim().EndsWith(":fx", StringComparison.OrdinalIgnoreCase)
            ? "https://api-free.deepl.com/v2/translate"
            : "https://api.deepl.com/v2/translate");

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is not null && !await consentService.HasConsentAsync(ConsentKey, cancellationToken).ConfigureAwait(false))
        {
            return new StageReadiness(
                StageName: StageNames.Translation,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
                ResolveAction: "grant-egress-consent");
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new StageReadiness(
                StageName: StageNames.Translation,
                Status: ReadinessState.CloudKeyMissing,
                Detail: $"{ProviderKey} API key is not configured",
                ModelId: null,
                ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Translation,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
            ResolveAction: null);
    }

    private async Task EnsureEgressConsentAsync(CancellationToken cancellationToken)
    {
        if (consentService is not null && !await consentService.HasConsentAsync(ConsentKey, cancellationToken).ConfigureAwait(false))
        {
            throw new CloudEgressConsentException(ConsentKey, ProviderName);
        }
    }

    private async Task<string> ResolveAuthKeyAsync(CancellationToken cancellationToken)
    {
        string? authKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(authKey))
        {
            throw new InvalidOperationException(
                "DeepL API key is not configured. Add a DeepL key in Cloud Models or set DEEPL_AUTH_KEY.");
        }

        return authKey.Trim();
    }

    private static string NormalizeTargetLanguageCode(string languageCode) =>
        NormalizeLanguageCode(languageCode)
            ?? throw new InvalidOperationException("DeepL target language is required.");

    private static string? NormalizeSourceLanguageCode(string languageCode)
    {
        string? normalized = NormalizeLanguageCode(languageCode);
        if (normalized is null || string.Equals(normalized, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int separatorIndex = normalized.IndexOf('-', StringComparison.Ordinal);
        return separatorIndex > 0 ? normalized[..separatorIndex] : normalized;
    }

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string normalized = languageCode.Trim().Replace('_', '-').ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }

    private static string ExtractErrorMessage(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return "No response body.";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("message", out JsonElement message) &&
                message.ValueKind is JsonValueKind.String)
            {
                return message.GetString() ?? responseBody;
            }
        }
        catch (JsonException)
        {
        }

        return responseBody;
    }

    private sealed record DeepLTranslateRequest(
        [property: JsonPropertyName("text")] IReadOnlyList<string> Text,
        [property: JsonPropertyName("target_lang")] string TargetLanguage,
        [property: JsonPropertyName("source_lang")] string? SourceLanguage);

    private sealed record DeepLTranslateResponse(
        [property: JsonPropertyName("translations")] DeepLTranslation[] Translations);

    private sealed record DeepLTranslation(
        [property: JsonPropertyName("detected_source_language")] string? DetectedSourceLanguage,
        [property: JsonPropertyName("text")] string Text);
}
