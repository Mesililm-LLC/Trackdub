using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Transcripts;

public sealed class GeminiCloudTextRefinementEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : ITextRefinementEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "gemini";
    public const string ProviderName = "gemini";
    public const string EngineFamilyName = "gemini-refinement-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.TextGemini;
    public const string DefaultModel = "gemini-3.5-flash-lite";
    public const string StandardModel = "gemini-2.5-flash";
    public const string AdvancedModel = "gemini-3.8-flash";

    private const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;

    public string EngineFamily => EngineFamilyName;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            return new StageReadiness(
                StageName: StageNames.TextRefinementAsr,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: EngineFamilyName,
                ResolveAction: "grant-egress-consent");
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new StageReadiness(
                StageName: StageNames.TextRefinementAsr,
                Status: ReadinessState.CloudKeyMissing,
                Detail: $"{ProviderKey} API key is not configured",
                ModelId: null,
                ModelAlias: EngineFamilyName,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.TextRefinementAsr,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: EngineFamilyName,
            ResolveAction: null);
    }

    public async Task<IReadOnlyList<RefinedTextSegment>> RefineAsync(
        TextRefinementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEgressConsent();
        if (request.Segments.Count == 0)
        {
            LastExecutionSummary = null;
            return [];
        }

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string model = ResolveModel(request);

        string systemInstruction = request.Scope switch
        {
            TextRefinementScope.Translation =>
                "You are a professional localization and dubbing dialogue polish engine. " +
                "Polish the translated dialogue segments in the JSON array for natural spoken cadence, " +
                "correct punctuation, and fluent dubbing delivery. Preserve original meaning and tone. " +
                "Return ONLY a valid JSON array of strings in the exact same order with the exact same count. No explanation, no markdown.",
            _ =>
                "You are an ASR transcription text polish engine. " +
                "Polish the transcribed speech segments in the JSON array: fix punctuation, capitalize proper nouns, " +
                "format numerals and dates cleanly, and correct obvious speech-to-text phonetic/homophone errors. " +
                "Do not summarize or translate. Preserve the original meaning and word order. " +
                "Return ONLY a valid JSON array of strings in the exact same order with the exact same count. No explanation, no markdown."
        };

        string userContent = JsonSerializer.Serialize(
            request.Segments.Select(s => s.Text).ToArray(),
            JsonOptions);

        GeminiRequest payload = new(
            SystemInstruction: new GeminiContent([new GeminiPart(systemInstruction)]),
            Contents: [new GeminiContent([new GeminiPart(userContent)])],
            GenerationConfig: new GeminiGenerationConfig("application/json"));

        string endpoint = $"{EndpointBase}/{model}:generateContent";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Add("x-goog-api-key", apiKey);
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
                $"Gemini text refinement failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        GeminiResponse? geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);
        string? content = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Gemini returned an empty text refinement response.");
        }

        string[]? refinedTexts = TryParseStringArray(StripMarkdownFences(content));
        if (refinedTexts is null || refinedTexts.Length != request.Segments.Count)
        {
            throw new InvalidOperationException(
                $"Gemini returned {refinedTexts?.Length ?? 0} refinement result(s) for {request.Segments.Count} input segment(s).");
        }

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelId: model,
            ModelAlias: EngineFamilyName,
            BootstrapDetail: "Google Gemini Cloud API");

        var results = new List<RefinedTextSegment>(request.Segments.Count);
        for (int i = 0; i < request.Segments.Count; i++)
        {
            TextRefinementInputSegment seg = request.Segments[i];
            string original = seg.Text;
            string refined = refinedTexts[i]?.Trim() ?? original;
            bool changed = !string.Equals(original, refined, StringComparison.Ordinal);

            results.Add(new RefinedTextSegment(
                seg.Index,
                seg.StartSeconds,
                seg.EndSeconds,
                OriginalText: original,
                RefinedText: refined,
                DisplayedText: refined,
                Accepted: true,
                GuardStatus: changed ? TextRefinementGuardStatus.Accepted : TextRefinementGuardStatus.Unchanged,
                AppliedCorrections: changed ? [TextRefinementCorrectionCodes.ModelPolishApplied] : [TextRefinementCorrectionCodes.FallbackUnchanged]));
        }

        return results;
    }

    private void EnsureEgressConsent()
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            throw new CloudEgressConsentException(ConsentKey, ProviderName);
        }
    }

    private static string ResolveModel(TextRefinementRequest request)
    {
        string? envModel = Environment.GetEnvironmentVariable("TRACKDUB_GEMINI_REFINEMENT_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            return envModel.Trim();
        }

        string? variant = request.PreferredModelVariantAlias ??
                          request.PreferredModelAlias ??
                          request.Options?.NormalizedPreferredModelVariantAlias ??
                          request.Options?.NormalizedPreferredModelAlias;

        if (variant is not null)
        {
            if (variant.Contains("3.8", StringComparison.OrdinalIgnoreCase))
            {
                return AdvancedModel;
            }

            if (variant.Contains("2.5", StringComparison.OrdinalIgnoreCase))
            {
                return StandardModel;
            }

            if (variant.Contains("lite", StringComparison.OrdinalIgnoreCase) ||
                variant.Contains("3.5", StringComparison.OrdinalIgnoreCase))
            {
                return DefaultModel;
            }
        }

        return DefaultModel;
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set GEMINI_API_KEY or TRACKDUB_GEMINI_API_KEY.");
        }

        return apiKey.Trim();
    }

    private static string StripMarkdownFences(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..];
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static string[]? TryParseStringArray(string content)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(content);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Select(e =>
                    {
                        if (e.ValueKind == JsonValueKind.String)
                        {
                            return e.GetString() ?? string.Empty;
                        }

                        if (e.ValueKind == JsonValueKind.Object)
                        {
                            if (e.TryGetProperty("refinedText", out JsonElement refinedTextProp) && refinedTextProp.ValueKind == JsonValueKind.String)
                            {
                                return refinedTextProp.GetString() ?? string.Empty;
                            }
                            if (e.TryGetProperty("text", out JsonElement textProp) && textProp.ValueKind == JsonValueKind.String)
                            {
                                return textProp.GetString() ?? string.Empty;
                            }
                            if (e.TryGetProperty("refined", out JsonElement refinedProp) && refinedProp.ValueKind == JsonValueKind.String)
                            {
                                return refinedProp.GetString() ?? string.Empty;
                            }
                        }

                        return e.ToString();
                    })
                    .ToArray();
            }
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"Failed to parse JSON string array content: {ex}");
        }

        return null;
    }

    private sealed record GeminiRequest(
        [property: JsonPropertyName("system_instruction")] GeminiContent SystemInstruction,
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] GeminiPart[] Parts,
        [property: JsonPropertyName("role")] string? Role = null);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("responseMimeType")] string ResponseMimeType);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);
}
