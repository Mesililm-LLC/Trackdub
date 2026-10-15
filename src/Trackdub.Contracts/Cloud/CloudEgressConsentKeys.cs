namespace Trackdub.Contracts.Cloud;

/// <summary>
/// Canonical consent keys formatted as "{egressType}:{providerKey}" (lowercased).
/// Audio, text, and full-media egress are tracked independently even for the same provider.
/// </summary>
public static class CloudEgressConsentKeys
{
    public const string AudioOpenAi = "audio:openai";
    public const string AudioGemini = "audio:gemini";
    public const string TextDeepL = "text:deepl";
    public const string TextOpenAi = "text:openai";
    public const string TextGemini = "text:gemini";
    public const string TextElevenLabs = "text:elevenlabs";
    public const string TextGoogle = "text:google";
    public const string MediaElevenLabs = "media:elevenlabs";

    public static IReadOnlyList<string> All { get; } =
    [
        AudioOpenAi,
        AudioGemini,
        TextDeepL,
        TextOpenAi,
        TextGemini,
        TextElevenLabs,
        TextGoogle,
        MediaElevenLabs,
    ];

    public static string Build(EgressType type, string providerKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        return $"{type.ToString().ToLowerInvariant()}:{providerKey.Trim().ToLowerInvariant()}";
    }
}
