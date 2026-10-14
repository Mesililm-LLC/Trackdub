using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Transcription;

public sealed class GeminiCloudTranscriptionEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : IAudioTranscriptionEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "gemini";
    public const string ProviderName = "gemini";
    public const string EngineFamilyName = "gemini-asr-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.AudioGemini;

    public const string DefaultModel = "gemini-3.8-flash";
    public const string QualityModel = "gemini-2.5-pro";
    public const string StandardModel = "gemini-2.5-flash";
    private const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            return new StageReadiness(
                StageName: StageNames.Asr,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: AsrModelOverrideSettings.GeminiAsrCloudAlias,
                ResolveAction: "grant-egress-consent");
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new StageReadiness(
                StageName: StageNames.Asr,
                Status: ReadinessState.CloudKeyMissing,
                Detail: $"{ProviderKey} API key is not configured",
                ModelId: null,
                ModelAlias: AsrModelOverrideSettings.GeminiAsrCloudAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Asr,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: AsrModelOverrideSettings.GeminiAsrCloudAlias,
            ResolveAction: null);
    }

    public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken) =>
        TranscribeAsync(new AudioTranscriptionRequest(normalizedAudioPath, regions), cancellationToken);

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEgressConsent();
        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        byte[] audioBytes = await File.ReadAllBytesAsync(
            request.NormalizedAudioPath, cancellationToken).ConfigureAwait(false);

        // Base64 expands raw bytes by ~4/3, so gate on the pre-encoding size that keeps the
        // encoded payload under Gemini's 20 MB inline limit rather than the raw file size.
        const long InlinePayloadLimitBytes = 20 * 1024 * 1024;
        const long MaxRawAudioBytes = InlinePayloadLimitBytes * 3 / 4;
        if (audioBytes.Length > MaxRawAudioBytes)
        {
            throw new InvalidOperationException(
                $"Audio file size ({audioBytes.Length / (1024 * 1024)} MB) would exceed Gemini's 20 MB inline " +
                "payload limit once base64-encoded. Please use compressed audio (MP3/OGG) or split into shorter segments.");
        }

        string base64Audio = Convert.ToBase64String(audioBytes);
        string extension = Path.GetExtension(request.NormalizedAudioPath).TrimStart('.').ToLowerInvariant();
        string mimeType = extension switch
        {
            "mp3" => "audio/mpeg",
            "ogg" => "audio/ogg",
            "flac" => "audio/flac",
            "m4a" => "audio/mp4",
            _ => "audio/wav"
        };

        const string prompt =
            "Transcribe this audio. Return a JSON array where each element is an object with these exact fields: " +
            "{\"start\": <float_seconds>, \"end\": <float_seconds>, \"text\": \"<string>\"}. " +
            "No markdown, no explanation, only a valid JSON array.";

        // Parts are polymorphic (inline_data vs text). System.Text.Json uses the declared type
        // for serialization, so object[] would produce empty {} for each element. Serialize each
        // part explicitly to JsonNode so the runtime type's properties are preserved.
        JsonNode[] parts =
        [
            JsonSerializer.SerializeToNode(new GeminiInlineDataPart(new GeminiInlineData(mimeType, base64Audio)), JsonOptions)!,
            JsonSerializer.SerializeToNode(new GeminiTextPart(prompt), JsonOptions)!
        ];

        GeminiRequest payload = new(
            Contents: [new GeminiContent(parts)],
            GenerationConfig: new GeminiGenerationConfig("application/json"));

        string model = ResolveModel(request);

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
                $"Gemini transcription failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        GeminiResponse? geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);
        string? content = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Gemini returned an empty transcription response.");
        }

        string cleanedContent = StripMarkdownFences(content);
        GeminiTranscriptSegment[]? segments = TryParseSegments(cleanedContent);
        if (segments is null || segments.Length == 0)
        {
            throw new InvalidOperationException($"Gemini transcription produced no parseable segments. Raw: {content[..Math.Min(200, content.Length)]}");
        }

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelId: model,
            ModelAlias: AsrModelOverrideSettings.GeminiAsrCloudAlias,
            BootstrapDetail: "Google Gemini Cloud API");

        return segments
            .Select((s, i) => new RecognizedTranscriptSegment(i, s.Start, s.End, s.Text.Trim()))
            .ToArray();
    }

    private void EnsureEgressConsent()
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            throw new CloudEgressConsentException(ConsentKey, ProviderName);
        }
    }

    private static string ResolveModel(AudioTranscriptionRequest request)
    {
        string? envModel = Environment.GetEnvironmentVariable("TRACKDUB_GEMINI_ASR_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            return envModel.Trim();
        }

        string? variant = request.Options?.NormalizedPreferredModelVariantAlias ?? request.Options?.NormalizedPreferredModelAlias;
        if (variant is not null)
        {
            if (variant.Contains("pro", StringComparison.OrdinalIgnoreCase))
            {
                return QualityModel;
            }

            if (variant.Contains("2.5", StringComparison.OrdinalIgnoreCase) ||
                variant.Contains("flash-2.5", StringComparison.OrdinalIgnoreCase))
            {
                return StandardModel;
            }

            if (variant.Contains("3.8", StringComparison.OrdinalIgnoreCase))
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

    private static GeminiTranscriptSegment[]? TryParseSegments(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<GeminiTranscriptSegment[]>(content, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Request types — Gemini parts are polymorphic; model as separate records with a union interface
    private sealed record GeminiRequest(
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] JsonNode[] Parts);

    private sealed record GeminiTextPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiInlineDataPart(
        [property: JsonPropertyName("inline_data")] GeminiInlineData InlineData);

    private sealed record GeminiInlineData(
        [property: JsonPropertyName("mime_type")] string MimeType,
        [property: JsonPropertyName("data")] string Data);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("responseMimeType")] string ResponseMimeType);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiResponseContent? Content);

    private sealed record GeminiResponseContent(
        [property: JsonPropertyName("parts")] GeminiResponsePart[]? Parts);

    private sealed record GeminiResponsePart(
        [property: JsonPropertyName("text")] string? Text);

    private sealed record GeminiTranscriptSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string Text);
}
