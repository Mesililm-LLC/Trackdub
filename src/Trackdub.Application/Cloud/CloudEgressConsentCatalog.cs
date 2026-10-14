using Trackdub.Contracts.Cloud;

namespace Trackdub.Application.Cloud;

/// <summary>
/// Static metadata catalog of all cloud data egress routes in Trackdub.
/// </summary>
public static class CloudEgressConsentCatalog
{
    public static IReadOnlyList<CloudEgressDescription> All { get; } =
    [
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.AudioOpenAi,
            EgressType: EgressType.Audio,
            ProviderDisplayName: "OpenAI Whisper",
            StageDisplayName: "Transcription (ASR)",
            DataDescription: "Full normalized audio file (WAV/MP3)",
            SentTo: "api.openai.com/v1/audio/transcriptions",
            PrivacyPolicyUrl: "https://openai.com/policies/privacy-policy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.AudioGemini,
            EgressType: EgressType.Audio,
            ProviderDisplayName: "Google Gemini",
            StageDisplayName: "Transcription (ASR)",
            DataDescription: "Full normalized audio file",
            SentTo: "generativelanguage.googleapis.com/v1beta/models",
            PrivacyPolicyUrl: "https://policies.google.com/privacy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.TextDeepL,
            EgressType: EgressType.Text,
            ProviderDisplayName: "DeepL",
            StageDisplayName: "Translation",
            DataDescription: "Transcript text segments",
            SentTo: "api.deepl.com/v2/translate",
            PrivacyPolicyUrl: "https://www.deepl.com/en/privacy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.TextOpenAi,
            EgressType: EgressType.Text,
            ProviderDisplayName: "OpenAI GPT + TTS",
            StageDisplayName: "Translation + TTS",
            DataDescription: "Transcript and translated text segments",
            SentTo: "api.openai.com/v1/chat/completions, api.openai.com/v1/audio/speech",
            PrivacyPolicyUrl: "https://openai.com/policies/privacy-policy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.TextGemini,
            EgressType: EgressType.Text,
            ProviderDisplayName: "Google Gemini",
            StageDisplayName: "Translation + Text Refinement + TTS",
            DataDescription: "Transcript and translated text segments",
            SentTo: "generativelanguage.googleapis.com/v1beta/models",
            PrivacyPolicyUrl: "https://policies.google.com/privacy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.TextElevenLabs,
            EgressType: EgressType.Text,
            ProviderDisplayName: "ElevenLabs TTS",
            StageDisplayName: "TTS",
            DataDescription: "Translated text per segment and voice ID",
            SentTo: "api.elevenlabs.io/v1/text-to-speech",
            PrivacyPolicyUrl: "https://elevenlabs.io/privacy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.TextGoogle,
            EgressType: EgressType.Text,
            ProviderDisplayName: "Google TTS",
            StageDisplayName: "TTS",
            DataDescription: "Translated text per segment",
            SentTo: "texttospeech.googleapis.com/v1/text:synthesize",
            PrivacyPolicyUrl: "https://policies.google.com/privacy"),
        new CloudEgressDescription(
            ConsentKey: CloudEgressConsentKeys.MediaElevenLabs,
            EgressType: EgressType.Media,
            ProviderDisplayName: "ElevenLabs Dubbing",
            StageDisplayName: "Full pipeline (Cloud Dubbing)",
            DataDescription: "Entire source media file (video+audio bytes, MP4/MKV/WAV)",
            SentTo: "api.elevenlabs.io/v1/dubbing",
            PrivacyPolicyUrl: "https://elevenlabs.io/privacy"),
    ];

    public static CloudEgressDescription? Find(string consentKey)
    {
        if (string.IsNullOrWhiteSpace(consentKey))
        {
            return null;
        }

        string normalized = consentKey.Trim().ToLowerInvariant();
        return All.FirstOrDefault(d => string.Equals(d.ConsentKey, normalized, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Alias for <see cref="CloudEgressConsentCatalog"/> matching design spec naming.
/// </summary>
public static class CloudEgressCatalog
{
    public static IReadOnlyList<CloudEgressDescription> All => CloudEgressConsentCatalog.All;

    public static CloudEgressDescription? Find(string consentKey) => CloudEgressConsentCatalog.Find(consentKey);
}
