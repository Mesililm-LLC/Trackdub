using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Onnx.Kokoro;

public sealed class KokoroVoiceCatalog : IVoiceCatalog
{
    public static readonly IReadOnlyList<string> KnownVoiceIds =
    [
        "af",
        "af_alloy",
        "af_aoede",
        "af_bella",
        "af_heart",
        "af_jessica",
        "af_kore",
        "af_nicole",
        "af_nova",
        "af_river",
        "af_sarah",
        "af_sky",
        "am_adam",
        "am_echo",
        "am_eric",
        "am_fenrir",
        "am_liam",
        "am_michael",
        "am_onyx",
        "am_puck",
        "am_santa",
        "bf_alice",
        "bf_emma",
        "bf_isabella",
        "bf_lily",
        "bm_daniel",
        "bm_fable",
        "bm_george",
        "bm_lewis",
        "ef_dora",
        "em_alex",
        "em_santa"
    ];

    private readonly string modelRootPath;
    private readonly IReadOnlyList<VoiceCatalogEntry> voices;

    private KokoroVoiceCatalog(string modelRootPath, IReadOnlyList<VoiceCatalogEntry> voices)
    {
        this.modelRootPath = modelRootPath;
        this.voices = voices;
    }

    /// <summary>No voices: the honest state when the Kokoro model is not installed.</summary>
    public static KokoroVoiceCatalog Empty() =>
        new(modelRootPath: string.Empty, []);

    public static KokoroVoiceCatalog KnownAvailable() =>
        new(
            modelRootPath: string.Empty,
            KnownVoiceIds
                .Select(static voiceId => TryParseVoiceEntry(voiceId, out VoiceCatalogEntry? entry) ? entry : null)
                .OfType<VoiceCatalogEntry>()
                .OrderBy(static voice => voice.VoiceId)
                .ToArray());

    public static async Task<KokoroVoiceCatalog> LoadAsync(string modelRootPath)
    {
        string voicesDirectory = Path.Join(modelRootPath, "voices");
        if (!Directory.Exists(voicesDirectory))
        {
            return new KokoroVoiceCatalog(modelRootPath, []);
        }

        var entries = new List<VoiceCatalogEntry>();
        await foreach (string binPath in EnumerateVoiceFilesAsync(voicesDirectory))
        {
            string voiceId = Path.GetFileNameWithoutExtension(binPath);
            if (TryParseVoiceEntry(voiceId, out VoiceCatalogEntry? entry))
            {
                entries.Add(entry);
            }
        }

        return new KokoroVoiceCatalog(modelRootPath, [.. entries.OrderBy(static v => v.VoiceId)]);
    }

    [Obsolete("Use LoadAsync instead.")]
    public static KokoroVoiceCatalog Load(string modelRootPath) =>
        LoadAsync(modelRootPath).ConfigureAwait(false).GetAwaiter().GetResult();

    private static async IAsyncEnumerable<string> EnumerateVoiceFilesAsync(string voicesDirectory)
    {
        var task = Task.Run(() => Directory.EnumerateFiles(voicesDirectory, "*.bin", SearchOption.TopDirectoryOnly).ToList());
        foreach (string file in await task)
        {
            yield return file;
        }
    }

    public static string ResolveRootContainingVoices(string modelRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRootPath);

        string current = Path.GetFullPath(modelRootPath);
        if (Directory.Exists(Path.Join(current, "voices")))
        {
            return current;
        }

        string? parent = Path.GetDirectoryName(current);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(Path.Join(parent, "voices")))
        {
            return parent;
        }

        return current;
    }

    public IReadOnlyList<VoiceCatalogEntry> GetVoices(string? languageCode = null)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return voices;
        }

        return [.. voices.Where(voice => IsLanguageMatch(voice.LanguageCode, languageCode))];
    }

    public bool TryGetVoice(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
    {
        entry = voices.FirstOrDefault(v => v.VoiceId == voiceId);
        return entry is not null;
    }

    internal string? GetBinPath(string voiceId)
    {
        string path = Path.Join(modelRootPath, "voices", $"{voiceId}.bin");
        return File.Exists(path) ? path : null;
    }

    private static bool TryParseVoiceEntry(string voiceId, [NotNullWhen(true)] out VoiceCatalogEntry? entry)
    {
        // Naming convention: {locale}{gender}_{name}  e.g. af_heart, bm_george
        entry = null;
        if (voiceId.Length == 2)
        {
            if (voiceId[1] is not ('f' or 'm'))
            {
                return false;
            }

            entry = new VoiceCatalogEntry(
                voiceId,
                MapLanguageCode(voiceId[0]),
                MapGender(voiceId[1]),
                "Default");
            return true;
        }

        if (voiceId.Length < 3 || voiceId[2] != '_')
        {
            return false;
        }

        string namePart = voiceId[3..];
        string displayName = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(namePart.Replace('_', ' '));

        entry = new VoiceCatalogEntry(voiceId, MapLanguageCode(voiceId[0]), MapGender(voiceId[1]), displayName);
        return true;
    }

    private static bool IsLanguageMatch(string voiceLanguageCode, string requestedLanguageCode)
    {
        string voice = voiceLanguageCode.Trim().Replace('_', '-').ToLowerInvariant();
        string requested = requestedLanguageCode.Trim().Replace('_', '-').ToLowerInvariant();
        if (voice.Length == 0 ||
            voice.Equals("mul", StringComparison.Ordinal) ||
            requested.Length == 0)
        {
            return true;
        }

        if (voice.Equals(requested, StringComparison.Ordinal))
        {
            return true;
        }

        return voice.StartsWith(requested + "-", StringComparison.Ordinal) ||
               requested.StartsWith(voice + "-", StringComparison.Ordinal);
    }

    private static string MapLanguageCode(char languagePrefix) =>
        languagePrefix switch
        {
            'a' => "en-us",
            'b' => "en-gb",
            'e' => "es",
            'f' => "fr",
            'h' => "hi",
            'i' => "it",
            'j' => "ja",
            'k' => "ko",
            'p' => "pt",
            'r' => "ru",
            'z' => "zh",
            _ => "unknown"
        };

    private static string MapGender(char genderPrefix) =>
        genderPrefix switch
        {
            'f' => "female",
            'm' => "male",
            _ => "unknown"
        };
}
