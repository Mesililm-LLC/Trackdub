using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Translation;

public sealed class OpenAiCloudTranslationEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : ITranslationEngine, ITranslationExecutionMetadataReporter, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "openai";
    public const string ProviderName = "openai";
    public const string EngineFamilyName = "openai-gpt-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.TextOpenAi;

    private const string ChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string DefaultModel = "gpt-4o-mini";

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
        EnsureEgressConsent();
        if (request.Segments.Count == 0)
        {
            return [];
        }

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        string systemPrompt =
            $"You are a translation engine. Translate the following JSON array of text segments from {request.SourceLanguage} to {request.TargetLanguage}. " +
            "Return ONLY a valid JSON array of translated strings in the same order, with exactly the same number of elements. No explanation, no markdown, no code fences.";

        string userContent = JsonSerializer.Serialize(
            request.Segments.Select(s => s.Text).ToArray(),
            JsonOptions);

        OpenAiChatRequest payload = new(
            DefaultModel,
            [
                new OpenAiMessage("system", systemPrompt),
                new OpenAiMessage("user", userContent)
            ],
            ResponseFormat: new OpenAiResponseFormat("json_object"));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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
                $"OpenAI translation failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        OpenAiChatResponse? chatResponse = JsonSerializer.Deserialize<OpenAiChatResponse>(responseBody, JsonOptions);
        string? content = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("OpenAI returned an empty translation response.");
        }

        string[]? translations = TryParseTranslationArray(content);
        if (translations is null || translations.Length != request.Segments.Count)
        {
            throw new InvalidOperationException(
                $"OpenAI returned {translations?.Length ?? 0} translation result(s) for {request.Segments.Count} input segment(s).");
        }

        LastExecutionMetadata = new TranslationExecutionMetadata(
            ProviderName,
            ModelId: DefaultModel,
            ModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            SelectedExecutionProvider: "cloud",
            TranslationRoutingKind.Direct);
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            BootstrapDetail: "OpenAI GPT Cloud API");

        var results = new List<TranslatedTextSegment>(request.Segments.Count);
        for (int i = 0; i < request.Segments.Count; i++)
        {
            TranslationInputSegment source = request.Segments[i];
            results.Add(new TranslatedTextSegment(
                source.Index,
                source.StartSeconds,
                source.EndSeconds,
                translations[i]));
        }

        return results;
    }

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            return new StageReadiness(
                StageName: StageNames.Translation,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias,
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
                ModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Translation,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            ResolveAction: null);
    }

    private void EnsureEgressConsent()
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            throw new CloudEgressConsentException(ConsentKey, ProviderName);
        }
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Set OPENAI_API_KEY or TRACKDUB_OPENAI_API_KEY.");
        }

        return apiKey.Trim();
    }

    private static string[]? TryParseTranslationArray(string content)
    {
        // Model may wrap in a JSON object: {"translations": [...]} or just return array directly
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .ToArray();
            }

            // Try common wrapper properties
            foreach (string prop in new[] { "translations", "translated", "results", "output" })
            {
                if (doc.RootElement.TryGetProperty(prop, out JsonElement arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    return arr.EnumerateArray()
                        .Select(e => e.GetString() ?? string.Empty)
                        .ToArray();
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed record OpenAiChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] OpenAiMessage[] Messages,
        [property: JsonPropertyName("response_format")] OpenAiResponseFormat? ResponseFormat = null);

    private sealed record OpenAiMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OpenAiResponseFormat(
        [property: JsonPropertyName("type")] string Type);

    private sealed record OpenAiChatResponse(
        [property: JsonPropertyName("choices")] OpenAiChoice[]? Choices);

    private sealed record OpenAiChoice(
        [property: JsonPropertyName("message")] OpenAiMessage? Message);
}
