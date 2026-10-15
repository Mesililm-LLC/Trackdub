using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Infrastructure.Tts;

public sealed class GeminiCloudTtsEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null)
    : ITtsEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "gemini";
    public const string ProviderName = "gemini";
    public const string EngineFamilyName = "gemini-tts-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.TextGemini;

    public const string DefaultModel = "gemini-3.8-flash-tts";
    public const string LiteModel = "gemini-3.8-flash-lite-tts";
    private const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models";
    private const int OutputSampleRate = 24000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is not null && !await consentService.HasConsentAsync(ConsentKey, cancellationToken).ConfigureAwait(false))
        {
            return new StageReadiness(
                StageName: StageNames.Tts,
                Status: ReadinessState.CloudEgressConsentRequired,
                Detail: $"Cloud egress to {ProviderName} ({ConsentKey}) requires user consent",
                ModelId: null,
                ModelAlias: TtsModelOverrideSettings.GeminiTtsCloudAlias,
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
                ModelAlias: TtsModelOverrideSettings.GeminiTtsCloudAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNames.Tts,
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: TtsModelOverrideSettings.GeminiTtsCloudAlias,
            ResolveAction: null);
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await EnsureEgressConsentAsync(cancellationToken).ConfigureAwait(false);
        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string model = ResolveModel(request);

        string voiceId = string.IsNullOrWhiteSpace(request.Voice.VoiceId)
            ? "Kore"
            : request.Voice.VoiceId.Trim();

        GeminiTtsRequest payload = new(
            Contents: [new GeminiTtsContent([new GeminiTtsPart(request.Text)])],
            GenerationConfig: new GeminiTtsGenerationConfig(
                ResponseModalities: ["AUDIO"],
                SpeechConfig: new GeminiSpeechConfig(
                    VoiceConfig: new GeminiVoiceConfig(
                        PrebuiltVoiceConfig: new GeminiPrebuiltVoiceConfig(voiceId)))));

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
                $"Gemini TTS failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        GeminiTtsResponse? ttsResponse = JsonSerializer.Deserialize<GeminiTtsResponse>(responseBody, JsonOptions);
        string? audioBase64 = ttsResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.InlineData?.Data;
        if (string.IsNullOrWhiteSpace(audioBase64))
        {
            throw new InvalidOperationException("Gemini TTS returned empty audio content.");
        }

        byte[] rawBytes = Convert.FromBase64String(audioBase64);
        byte[] wavBytes;
        int durationSamples;

        // Check if rawBytes is already a WAV container (starts with "RIFF")
        if (rawBytes.Length >= 44 &&
            rawBytes[0] == (byte)'R' && rawBytes[1] == (byte)'I' &&
            rawBytes[2] == (byte)'F' && rawBytes[3] == (byte)'F')
        {
            wavBytes = rawBytes;
            uint dataChunkSize = BitConverter.ToUInt32(rawBytes, 40);
            durationSamples = (int)(dataChunkSize / 2);
        }
        else
        {
            wavBytes = WrapPcmInWav(rawBytes, OutputSampleRate, channels: 1, bitsPerSample: 16);
            durationSamples = rawBytes.Length / 2;
        }

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelId: model,
            ModelAlias: TtsModelOverrideSettings.GeminiTtsCloudAlias,
            BootstrapDetail: "Google Gemini Cloud TTS API");

        return new TtsSynthesisResult(
            WavBytes: wavBytes,
            DurationSamples: durationSamples,
            SampleRate: OutputSampleRate,
            ModelId: model,
            VoiceId: voiceId,
            Provider: ProviderName);
    }

    private async Task EnsureEgressConsentAsync(CancellationToken cancellationToken)
    {
        if (consentService is not null && !await consentService.HasConsentAsync(ConsentKey, cancellationToken).ConfigureAwait(false))
        {
            throw new CloudEgressConsentException(ConsentKey, ProviderName);
        }
    }

    private static string ResolveModel(TtsSynthesisRequest request)
    {
        string? envModel = Environment.GetEnvironmentVariable("TRACKDUB_GEMINI_TTS_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            return envModel.Trim();
        }

        string? variant = request.Options?.NormalizedPreferredModelVariantAlias ?? request.Options?.NormalizedPreferredModelAlias;
        if (variant is not null)
        {
            if (variant.Contains("lite", StringComparison.OrdinalIgnoreCase))
            {
                return LiteModel;
            }

            if (variant.Contains("flash", StringComparison.OrdinalIgnoreCase))
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

    private sealed record GeminiTtsRequest(
        [property: JsonPropertyName("contents")] GeminiTtsContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiTtsGenerationConfig GenerationConfig);

    private sealed record GeminiTtsContent(
        [property: JsonPropertyName("parts")] GeminiTtsPart[] Parts);

    private sealed record GeminiTtsPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiTtsGenerationConfig(
        [property: JsonPropertyName("responseModalities")] string[] ResponseModalities,
        [property: JsonPropertyName("speechConfig")] GeminiSpeechConfig SpeechConfig);

    private sealed record GeminiSpeechConfig(
        [property: JsonPropertyName("voiceConfig")] GeminiVoiceConfig VoiceConfig);

    private sealed record GeminiVoiceConfig(
        [property: JsonPropertyName("prebuiltVoiceConfig")] GeminiPrebuiltVoiceConfig PrebuiltVoiceConfig);

    private sealed record GeminiPrebuiltVoiceConfig(
        [property: JsonPropertyName("voiceName")] string VoiceName);

    private sealed record GeminiTtsResponse(
        [property: JsonPropertyName("candidates")] GeminiTtsCandidate[]? Candidates);

    private sealed record GeminiTtsCandidate(
        [property: JsonPropertyName("content")] GeminiTtsResponseContent? Content);

    private sealed record GeminiTtsResponseContent(
        [property: JsonPropertyName("parts")] GeminiTtsResponsePart[]? Parts);

    private sealed record GeminiTtsResponsePart(
        [property: JsonPropertyName("inlineData")] GeminiTtsInlineData? InlineData);

    private sealed record GeminiTtsInlineData(
        [property: JsonPropertyName("mimeType")] string? MimeType,
        [property: JsonPropertyName("data")] string? Data);
}
