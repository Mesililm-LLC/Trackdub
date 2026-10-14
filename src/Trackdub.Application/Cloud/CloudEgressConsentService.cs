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

    public CloudEgressConsentService(
        IStudioSettingsService settingsService,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public bool HasConsent(string consentKey)
    {
        if (string.IsNullOrWhiteSpace(consentKey))
        {
            return false;
        }

        string normalizedKey = consentKey.Trim().ToLowerInvariant();

        StudioSettings settings = _settingsService
            .LoadAsync(CancellationToken.None)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

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
        if (string.Equals(alias, "gemini-refinement-cloud", StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith("gemini-refinement", StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith("gemini-3.8", StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith("gemini-3.5", StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase) ||
            alias.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase))
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
        foreach (string variable in new[] { ConsentEnvironmentVariable, ConsentsEnvironmentVariable })
        {
            string? raw = _getEnvironmentVariable(variable);
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
