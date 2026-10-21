using Trackdub.Sdk;

namespace Trackdub.Cli;

/// <summary>Versioned application preset. Host qualification is a separate acceptance step.</summary>
internal static class PocServerPreset
{
    internal const string Name = "poc-server-v1";
    internal static PipelinePreset Create() => new()
    {
        Version = 1, SourceLanguage = "en", TargetLanguage = "es", ExportFormat = "mp4", EnableAsrTextRefinement = false,
        Models = new Dictionary<string, string>
        {
            ["vad"] = "silero-vad", ["asr"] = "parakeet-tdt-0.6b-v3", ["translation"] = "madlad400",
            ["tts"] = "kokoro", ["diarization"] = "sortformer-4spk", ["separation"] = "spleeter",
            ["speech-enhancement"] = "deepfilternet3",
        },
    };
    internal static IReadOnlyDictionary<string, string> Providers { get; } = new Dictionary<string, string>
    {
        ["vad"] = "cpu", ["asr"] = "cuda", ["translation"] = "cuda", ["tts"] = "cuda",
        ["diarization"] = "cuda", ["separation"] = "cuda", ["speech-enhancement"] = "cuda",
    };
    internal static IReadOnlyDictionary<string, string> Variants { get; } = new Dictionary<string, string>
    { ["translation"] = "quantized", ["tts"] = "default" };
}
