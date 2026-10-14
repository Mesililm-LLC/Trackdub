using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Tts;

public sealed class GoogleCloudTtsEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : ITtsEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "google";
    public const string ProviderName = "google";
    public const string EngineFamilyName = "google-tts-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.TextGoogle;

    private const string SynthesizeEndpoint = "https://texttospeech.googleapis.com/v1/text:synthesize";
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
        string languageCode = NormalizeLanguageCode(request.LanguageCode);

        GoogleTtsRequest payload = new(
            Input: new GoogleTtsInput(request.Text),
            Voice: new GoogleTtsVoice(languageCode, Name: voiceId),
            AudioConfig: new GoogleTtsAudioConfig("LINEAR16", OutputSampleRate));

        string endpoint = $"{SynthesizeEndpoint}?key={Uri.EscapeDataString(apiKey)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
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
                $"Google Cloud TTS failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        GoogleTtsResponse? ttsResponse = JsonSerializer.Deserialize<GoogleTtsResponse>(responseBody, JsonOptions);
        if (string.IsNullOrWhiteSpace(ttsResponse?.AudioContent))
        {
            throw new InvalidOperationException("Google Cloud TTS returned empty audio content.");
        }

        byte[] pcmBytes = Convert.FromBase64String(ttsResponse.AudioContent);
        byte[] wavBytes = WrapPcmInWav(pcmBytes, OutputSampleRate, channels: 1, bitsPerSample: 16);

        int durationSamples = pcmBytes.Length / 2; // LINEAR16 = 16-bit = 2 bytes per sample

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelAlias: TtsModelOverrideSettings.GoogleTtsCloudAlias,
            BootstrapDetail: "Google Cloud TTS API");

        return new TtsSynthesisResult(
            WavBytes: wavBytes,
            DurationSamples: durationSamples,
            SampleRate: OutputSampleRate,
            ModelId: voiceId,
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
                ModelAlias: TtsModelOverrideSettings.GoogleTtsCloudAlias,
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
                ModelAlias: TtsModelOverrideSettings.GoogleTtsCloudAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Tts,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: TtsModelOverrideSettings.GoogleTtsCloudAlias,
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
                "Google API key is not configured. Set GOOGLE_API_KEY or TRACKDUB_GOOGLE_API_KEY.");
        }

        return apiKey.Trim();
    }

    private static string NormalizeLanguageCode(string languageCode)
    {
        // Google expects BCP-47 like "en-US"; keep as-is if already formatted
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return "en-US";
        }

        string normalized = languageCode.Trim();
        // If only 2-letter code, append "-" + uppercase for common cases
        if (normalized.Length == 2)
        {
            normalized = normalized.ToLowerInvariant() switch
            {
                "en" => "en-US",
                "es" => "es-ES",
                "fr" => "fr-FR",
                "de" => "de-DE",
                "ja" => "ja-JP",
                "zh" => "zh-CN",
                "ko" => "ko-KR",
                "pt" => "pt-BR",
                "ru" => "ru-RU",
                "it" => "it-IT",
                _ => $"{normalized.ToLowerInvariant()}-{normalized.ToUpperInvariant()}"
            };
        }

        return normalized;
    }

    private static byte[] WrapPcmInWav(byte[] pcmBytes, int sampleRate, short channels, short bitsPerSample)
    {
        int dataSize = pcmBytes.Length;
        byte[] wav = new byte[44 + dataSize];

        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);

        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);
        BitConverter.GetBytes(channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * channels * bitsPerSample / 8).CopyTo(wav, 28);
        BitConverter.GetBytes((short)(channels * bitsPerSample / 8)).CopyTo(wav, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(wav, 34);

        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataSize).CopyTo(wav, 40);
        pcmBytes.CopyTo(wav, 44);

        return wav;
    }

    private sealed record GoogleTtsRequest(
        [property: JsonPropertyName("input")] GoogleTtsInput Input,
        [property: JsonPropertyName("voice")] GoogleTtsVoice Voice,
        [property: JsonPropertyName("audioConfig")] GoogleTtsAudioConfig AudioConfig);

    private sealed record GoogleTtsInput(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GoogleTtsVoice(
        [property: JsonPropertyName("languageCode")] string LanguageCode,
        [property: JsonPropertyName("name")] string Name);

    private sealed record GoogleTtsAudioConfig(
        [property: JsonPropertyName("audioEncoding")] string AudioEncoding,
        [property: JsonPropertyName("sampleRateHertz")] int SampleRateHertz);

    private sealed record GoogleTtsResponse(
        [property: JsonPropertyName("audioContent")] string? AudioContent);
}
