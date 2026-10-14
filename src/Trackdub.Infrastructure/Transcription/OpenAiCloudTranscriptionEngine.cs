using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Transcription;

public sealed class OpenAiCloudTranscriptionEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : IAudioTranscriptionEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "openai";
    public const string ProviderName = "openai";
    public const string EngineFamilyName = "openai-whisper-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.AudioOpenAi;

    private const string TranscriptionsEndpoint = "https://api.openai.com/v1/audio/transcriptions";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

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
        await EnsureEgressConsentAsync(cancellationToken).ConfigureAwait(false);
        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        byte[] audioBytes = await File.ReadAllBytesAsync(
            request.NormalizedAudioPath, cancellationToken).ConfigureAwait(false);

        string fileName = Path.GetFileName(request.NormalizedAudioPath);
        string extension = Path.GetExtension(request.NormalizedAudioPath).TrimStart('.').ToLowerInvariant();
        string mimeType = extension is "mp3" ? "audio/mpeg" : "audio/wav";

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(audioBytes) { Headers = { ContentType = new(mimeType) } }, "file", fileName);
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new StringContent("verbose_json"), "response_format");
        form.Add(new StringContent("segment"), "timestamp_granularities[]");

        if (!string.IsNullOrWhiteSpace(request.SourceLanguage) &&
            !string.Equals(request.SourceLanguage, "auto", StringComparison.OrdinalIgnoreCase))
        {
            // Whisper expects ISO 639-1 (2-letter); take first part if format is "en-US"
            string langCode = request.SourceLanguage.Trim().Split('-')[0].ToLowerInvariant();
            form.Add(new StringContent(langCode), "language");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, TranscriptionsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = form;

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        string responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"OpenAI transcription failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        WhisperVerboseResponse? whisperResponse = JsonSerializer.Deserialize<WhisperVerboseResponse>(
            responseBody, JsonOptions);

        if (whisperResponse?.Segments is null)
        {
            throw new InvalidOperationException("OpenAI Whisper returned no segments.");
        }

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelAlias: AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
            BootstrapDetail: "OpenAI Whisper Cloud API");

        return whisperResponse.Segments
            .Select((s, i) => new RecognizedTranscriptSegment(
                i,
                s.Start,
                s.End,
                s.Text.Trim(),
                DetectedLanguage: whisperResponse.Language))
            .ToArray();
    }

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is not null && !await consentService.HasConsentAsync(ConsentKey, cancellationToken).ConfigureAwait(false))
        {
            return new StageReadiness(
                StageName: StageNames.Asr,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
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
                ModelAlias: AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Asr,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
            ResolveAction: null);
    }

    private async Task EnsureEgressConsentAsync(CancellationToken cancellationToken)
    {
        if (consentService is not null && !await consentService.HasConsentAsync(ConsentKey, cancellationToken).ConfigureAwait(false))
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

    private sealed record WhisperVerboseResponse(
        [property: JsonPropertyName("language")] string? Language,
        [property: JsonPropertyName("segments")] WhisperSegment[]? Segments);

    private sealed record WhisperSegment(
        [property: JsonPropertyName("start")] double Start,
        [property: JsonPropertyName("end")] double End,
        [property: JsonPropertyName("text")] string Text);
}
