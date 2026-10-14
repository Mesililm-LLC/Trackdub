using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Tts;

public sealed class OpenAiCloudTtsEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : ITtsEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "openai";
    public const string ProviderName = "openai";
    public const string EngineFamilyName = "openai-tts-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.TextOpenAi;

    private const string SpeechEndpoint = "https://api.openai.com/v1/audio/speech";
    private const string DefaultModel = "tts-1-hd";
    // OpenAI TTS-1-HD outputs 24 kHz PCM WAV
    private const int OutputSampleRate = 24000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEgressConsent();
        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        string voiceId = request.Voice.VoiceId;

        OpenAiSpeechRequest payload = new(
            Model: DefaultModel,
            Input: request.Text,
            Voice: voiceId,
            ResponseFormat: "wav");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, SpeechEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"OpenAI TTS failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorBody}");
        }

        byte[] wavBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        // WAV data chunk size is at offset 40 (4 bytes, little-endian); each sample is 16-bit = 2 bytes
        int durationSamples = wavBytes.Length >= 44
            ? (int)(BitConverter.ToUInt32(wavBytes, 40) / 2)
            : wavBytes.Length / 2;

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelAlias: TtsModelOverrideSettings.OpenAiTtsCloudAlias,
            BootstrapDetail: "OpenAI TTS Cloud API");

        return new TtsSynthesisResult(
            WavBytes: wavBytes,
            DurationSamples: durationSamples,
            SampleRate: OutputSampleRate,
            ModelId: DefaultModel,
            VoiceId: voiceId,
            Provider: ProviderName);
    }

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            return new StageReadiness(
                StageName: StageNames.Tts,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: TtsModelOverrideSettings.OpenAiTtsCloudAlias,
                ResolveAction: "grant-egress-consent");
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new StageReadiness(
                StageName: StageNames.Tts,
                Status: ReadinessState.CloudKeyMissing,
                Detail: $"{ProviderKey} API key is not configured",
                ModelId: null,
                ModelAlias: TtsModelOverrideSettings.OpenAiTtsCloudAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Tts,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: TtsModelOverrideSettings.OpenAiTtsCloudAlias,
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

    private sealed record OpenAiSpeechRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string Input,
        [property: JsonPropertyName("voice")] string Voice,
        [property: JsonPropertyName("response_format")] string ResponseFormat);
}
