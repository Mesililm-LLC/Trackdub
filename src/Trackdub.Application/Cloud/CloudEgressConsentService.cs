using Trackdub.Contracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Cloud;

/// <summary>
/// Persists and queries per-(egressType, provider) cloud data egress consent via <see cref="IStudioSettingsService"/>.
/// </summary>
public sealed class CloudEgressConsentService : ICloudEgressConsentService
{
    public const string ConsentEnvironmentVariable = "TRACKDUB_CLOUD_EGRESS_CONSENT";
    public const string ConsentsEnvironmentVariable = "TRACKDUB_CLOUD_EGRESS_CONSENTS";

    private readonly IStudioSettingsService _settingsService;
    private readonly Func<string, string?> _getEnvironmentVariable;

    // Cached settings snapshot so per-segment TTS/ASR/translation calls do not re-read and
    // re-parse settings.json on every consent check. Invalidated on SetConsentAsync so
    // newly granted (or revoked) consent is observed immediately. Environment-variable
    // consent is still evaluated per call, so it needs no invalidation.
    private StudioSettings? _cachedSettings;
    private bool _cacheValid;

    public CloudEgressConsentService(
        IStudioSettingsService settingsService,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public async Task<bool> HasConsentAsync(string consentKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(consentKey))
        {
            return false;
        }

        string normalizedKey = consentKey.Trim().ToLowerInvariant();

        StudioSettings settings = await GetSettingsAsync(ct).ConfigureAwait(false);

        if (settings.CloudEgressConsents is not null)
        {
            if (settings.CloudEgressConsents.TryGetValue(normalizedKey, out bool explicitValue))
            {
                return explicitValue;
            }

            foreach ((string key, bool consented) in settings.CloudEgressConsents)
            {
                if (string.Equals(key?.Trim(), normalizedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return consented;
                }
            }
        }

        return HasEnvironmentConsent(normalizedKey);
    }

    private async Task<StudioSettings> GetSettingsAsync(CancellationToken ct)
    {
        if (_cacheValid && _cachedSettings is not null)
        {
            return _cachedSettings;
        }

        StudioSettings settings = await _settingsService.LoadAsync(ct).ConfigureAwait(false);
        _cachedSettings = settings;
        _cacheValid = true;
        return settings;
    }

    public IReadOnlyList<string> GetRequiredConsentKeys(string stageName, string? modelAlias)
    {
        if (string.IsNullOrWhiteSpace(stageName) || string.IsNullOrWhiteSpace(modelAlias))
        {
            return [];
        }

        string normalizedStage = stageName.Trim().ToLowerInvariant();
        string normalizedAlias = modelAlias.Trim();

        return normalizedStage switch
        {
            StageNames.Asr => ResolveAsrConsentKeys(normalizedAlias),
            StageNames.Translation => ResolveTranslationConsentKeys(normalizedAlias),
            StageNames.Tts => ResolveTtsConsentKeys(normalizedAlias),
            StageNames.TextRefinement
                or StageNames.TextRefinementAsr
                or StageNames.TextRefinementTranslation
                or "textrefinement" => ResolveTextRefinementConsentKeys(normalizedAlias),
            "cloud-dub" or "dubbing" or "elevenlabs-dubbing" => ResolveCloudDubbingConsentKeys(normalizedAlias),
            _ => [],
        };
    }

    public async Task SetConsentAsync(string consentKey, bool consented, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consentKey);
        string normalizedKey = consentKey.Trim().ToLowerInvariant();

        StudioSettings current = await _settingsService.LoadAsync(ct).ConfigureAwait(false);
        var updatedConsents = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (current.CloudEgressConsents is not null)
        {
            foreach ((string key, bool value) in current.CloudEgressConsents)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    updatedConsents[key.Trim().ToLowerInvariant()] = value;
                }
            }
        }

        updatedConsents[normalizedKey] = consented;
        StudioSettings updated = current with { CloudEgressConsents = updatedConsents };
        _cachedSettings = null;
        _cacheValid = false;
        await _settingsService.SaveAsync(updated, ct).ConfigureAwait(false);
    }

    private static IReadOnlyList<string> ResolveAsrConsentKeys(string alias)
    {
        if (AsrModelOverrideSettings.IsOpenAiWhisperAlias(alias))
        {
            return [CloudEgressConsentKeys.AudioOpenAi];
        }

        if (AsrModelOverrideSettings.IsGeminiAsrAlias(alias))
        {
            return [CloudEgressConsentKeys.AudioGemini];
        }

        return [];
    }

    private static IReadOnlyList<string> ResolveTranslationConsentKeys(string alias)
    {
        if (TranslationModelOverrideSettings.IsDeepLModelAlias(alias))
        {
            return [CloudEgressConsentKeys.TextDeepL];
        }

        if (TranslationModelOverrideSettings.IsOpenAiGptAlias(alias))
        {
            return [CloudEgressConsentKeys.TextOpenAi];
        }

        if (TranslationModelOverrideSettings.IsGeminiTranslationAlias(alias))
        {
            return [CloudEgressConsentKeys.TextGemini];
        }

        return [];
    }

    private static IReadOnlyList<string> ResolveTtsConsentKeys(string alias)
    {
        if (TtsModelOverrideSettings.IsElevenLabsAlias(alias))
        {
            return [CloudEgressConsentKeys.TextElevenLabs];
        }

        if (TtsModelOverrideSettings.IsOpenAiTtsAlias(alias))
        {
            return [CloudEgressConsentKeys.TextOpenAi];
        }

        if (TtsModelOverrideSettings.IsGoogleTtsAlias(alias))
        {
            return [CloudEgressConsentKeys.TextGoogle];
        }

        if (TtsModelOverrideSettings.IsGeminiTtsAlias(alias))
        {
            return [CloudEgressConsentKeys.TextGemini];
        }

        return [];
    }

    private static IReadOnlyList<string> ResolveTextRefinementConsentKeys(string alias)
    {
        // Matches RoutedTextRefinementEngine: any alias beginning with "gemini" routes to the
        // Gemini cloud refinement engine, so readiness and the per-call consent check agree.
        if (string.Equals(alias, "gemini-refinement-cloud", StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
        {
            return [CloudEgressConsentKeys.TextGemini];
        }

        return [];
    }

    private static IReadOnlyList<string> ResolveCloudDubbingConsentKeys(string alias)
    {
        if (string.Equals(alias, "elevenlabs", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(alias, "elevenlabs-dubbing", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(alias, "elevenlabs-dubbing-cloud", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(alias, CloudEgressConsentKeys.MediaElevenLabs, StringComparison.OrdinalIgnoreCase))
        {
            return [CloudEgressConsentKeys.MediaElevenLabs];
        }

        return [];
    }

    private bool HasEnvironmentConsent(string normalizedKey)
    {
        foreach (string? raw in new[] { ConsentEnvironmentVariable, ConsentsEnvironmentVariable }.Select(_getEnvironmentVariable))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(token, normalizedKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
