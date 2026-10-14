using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Infrastructure.Dubbing;

public sealed class ElevenLabsCloudDubbingEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider,
    ICloudEgressConsentService? consentService = null,
    TimeSpan? pollInterval = null)
    : ICloudDubbingEngine
{
    public const string ProviderKey = "elevenlabs";
    public const string ProviderName = "elevenlabs";
    public const string EngineFamilyName = "elevenlabs-dubbing-cloud";
    public const string ConsentKey = CloudEgressConsentKeys.MediaElevenLabs;

    private const string ApiBase = "https://api.elevenlabs.io/v1";
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
    private readonly ICloudEgressConsentService? consentService = consentService;
    private readonly TimeSpan pollInterval = pollInterval ?? DefaultPollInterval;

    public async Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            return new StageReadiness(
                StageName: "cloud-dub",
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
                StageName: "cloud-dub",
                Status: ReadinessState.CloudKeyMissing,
                Detail: $"{ProviderKey} API key is not configured",
                ModelId: null,
                ModelAlias: EngineFamilyName,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: "cloud-dub",
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: EngineFamilyName,
            ResolveAction: null);
    }

    public async Task<CloudDubbingResult> DubAsync(
        CloudDubbingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureEgressConsent();

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        IProgress<CloudDubbingProgress>? progress = request.Progress;

        progress?.Report(new CloudDubbingProgress(
            CloudDubbingStage.Submitting,
            "Submitting media file to ElevenLabs Dubbing API."));

        // Step 1 — Submit dubbing job
        (string dubbingId, double expectedDurationSec) = await SubmitDubbingJobAsync(
            request, apiKey, cancellationToken).ConfigureAwait(false);

        bool remoteDeleted = false;
        try
        {
            progress?.Report(new CloudDubbingProgress(
                CloudDubbingStage.Polling,
                $"Polling ElevenLabs dubbing job '{dubbingId}'.",
                dubbingId));

            // Step 2 — Poll until complete
            await PollUntilDubbedAsync(dubbingId, apiKey, progress, cancellationToken).ConfigureAwait(false);

            progress?.Report(new CloudDubbingProgress(
                CloudDubbingStage.Downloading,
                $"Downloading dubbed audio for '{dubbingId}'.",
                dubbingId));

            // Step 3 — Download dubbed audio
            byte[] audioBytes = await DownloadDubbedAudioAsync(
                dubbingId, request.TargetLanguage, apiKey, cancellationToken).ConfigureAwait(false);

            if (request.DeleteRemoteProjectOnCompletion)
            {
                progress?.Report(new CloudDubbingProgress(
                    CloudDubbingStage.DeletingRemoteProject,
                    $"Deleting remote dubbing project '{dubbingId}' from ElevenLabs.",
                    dubbingId));

                remoteDeleted = await TryDeleteDubbingProjectInternalAsync(
                    dubbingId, apiKey, cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(new CloudDubbingProgress(
                CloudDubbingStage.Completed,
                $"Completed cloud dubbing job '{dubbingId}'.",
                dubbingId));

            return new CloudDubbingResult(
                AudioBytes: audioBytes,
                TargetLanguage: request.TargetLanguage,
                EstimatedDuration: TimeSpan.FromSeconds(expectedDurationSec),
                DubbingId: dubbingId,
                RemoteProjectDeleted: remoteDeleted);
        }
        catch
        {
            if (request.DeleteRemoteProjectOnCompletion && !remoteDeleted)
            {
                await TryDeleteDubbingProjectInternalAsync(
                    dubbingId, apiKey, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task DeleteDubbingProjectAsync(
        string dubbingId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dubbingId);
        EnsureEgressConsent();
        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Delete,
            $"{ApiBase}/dubbing/{Uri.EscapeDataString(dubbingId.Trim())}");
        httpRequest.Headers.Add("xi-api-key", apiKey);

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ElevenLabs dubbing project deletion failed for '{dubbingId}' with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorBody}");
        }
    }

    private void EnsureEgressConsent()
    {
        if (consentService is null || !consentService.HasConsent(ConsentKey))
        {
            throw new CloudEgressConsentException(ConsentKey, ProviderName);
        }
    }

    private async Task<bool> TryDeleteDubbingProjectInternalAsync(
        string dubbingId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        try
        {
            using var httpRequest = new HttpRequestMessage(
                HttpMethod.Delete,
                $"{ApiBase}/dubbing/{Uri.EscapeDataString(dubbingId)}");
            httpRequest.Headers.Add("xi-api-key", apiKey);

            using HttpResponseMessage response = await httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<(string DubbingId, double ExpectedDurationSec)> SubmitDubbingJobAsync(
        CloudDubbingRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(
            request.MediaFilePath, cancellationToken).ConfigureAwait(false);

        string fileName = Path.GetFileName(request.MediaFilePath);
        string extension = Path.GetExtension(request.MediaFilePath).TrimStart('.').ToLowerInvariant();
        string mimeType = extension switch
        {
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            _ => "application/octet-stream"
        };

        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(fileBytes) { Headers = { ContentType = new(mimeType) } },
            "file",
            fileName);
        form.Add(new StringContent(request.SourceLanguage), "source_lang");
        form.Add(new StringContent(request.TargetLanguage), "target_lang");
        form.Add(new StringContent("automatic"), "mode");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/dubbing");
        httpRequest.Headers.Add("xi-api-key", apiKey);
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
                $"ElevenLabs dubbing submission failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        ElevenLabsDubbingSubmitResponse? submitResponse =
            JsonSerializer.Deserialize<ElevenLabsDubbingSubmitResponse>(responseBody, JsonOptions);

        if (string.IsNullOrWhiteSpace(submitResponse?.DubbingId))
        {
            throw new InvalidOperationException(
                $"ElevenLabs did not return a dubbing_id. Response: {responseBody}");
        }

        return (submitResponse.DubbingId, submitResponse.ExpectedDurationSec ?? 0d);
    }

    private async Task PollUntilDubbedAsync(
        string dubbingId,
        string apiKey,
        IProgress<CloudDubbingProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + MaxWait;
        using var timer = new PeriodicTimer(pollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"ElevenLabs dubbing job '{dubbingId}' did not complete within {MaxWait.TotalMinutes} minutes.");
            }

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/dubbing/{dubbingId}");
            statusRequest.Headers.Add("xi-api-key", apiKey);

            using HttpResponseMessage statusResponse = await httpClient
                .SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string statusBody = statusResponse.Content is null
                ? string.Empty
                : await statusResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!statusResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"ElevenLabs dubbing status check failed with HTTP {(int)statusResponse.StatusCode}: {statusBody}");
            }

            ElevenLabsDubbingStatusResponse? statusResult =
                JsonSerializer.Deserialize<ElevenLabsDubbingStatusResponse>(statusBody, JsonOptions);

            string? status = statusResult?.Status?.ToLowerInvariant();
            switch (status)
            {
                case "dubbed":
                    return;
                case "failed":
                    throw new InvalidOperationException(
                        $"ElevenLabs dubbing job '{dubbingId}' failed. Response: {statusBody}");
                default:
                    progress?.Report(new CloudDubbingProgress(
                        CloudDubbingStage.Polling,
                        $"ElevenLabs dubbing job '{dubbingId}' status: {status ?? "pending"}.",
                        dubbingId));
                    break;
            }
        }
    }

    private async Task<byte[]> DownloadDubbedAudioAsync(
        string dubbingId,
        string languageCode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        string downloadUrl = $"{ApiBase}/dubbing/{dubbingId}/audio/{Uri.EscapeDataString(languageCode)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        httpRequest.Headers.Add("xi-api-key", apiKey);

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ElevenLabs dubbed audio download failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "ElevenLabs API key is not configured. Set ELEVENLABS_API_KEY or TRACKDUB_ELEVENLABS_API_KEY.");
        }

        return apiKey.Trim();
    }

    private sealed record ElevenLabsDubbingSubmitResponse(
        [property: JsonPropertyName("dubbing_id")] string? DubbingId,
        [property: JsonPropertyName("expected_duration_sec")] double? ExpectedDurationSec);

    private sealed record ElevenLabsDubbingStatusResponse(
        [property: JsonPropertyName("dubbing_id")] string? DubbingId,
        [property: JsonPropertyName("status")] string? Status);
}
