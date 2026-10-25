namespace Trackdub.Media.LipSynthesis.NvidiaMaxine;

/// <summary>
/// Where to find the NVIDIA Maxine AR SDK and the LipSync host executable. Nothing NVIDIA ships is
/// bundled with Trackdub; users point it at a local AR SDK install that has the LipSync,
/// FaceBoxDetection, LandmarkDetection and FaceExpressions features and their models.
/// </summary>
public sealed record NvidiaMaxineLipSyncOptions(
    string? HostExecutablePath,
    string? ArSdkRoot,
    NvidiaMaxineLipSyncLanguage DefaultLanguage = NvidiaMaxineLipSyncLanguage.Generic,
    TimeSpan? PerTurnTimeout = null)
{
    public const string HostExecutableEnvironmentVariable = "TRACKDUB_MAXINE_LIPSYNC_HOST";
    public const string ArSdkRootEnvironmentVariable = "TRACKDUB_MAXINE_ARSDK_ROOT";
    public const string LanguageEnvironmentVariable = "TRACKDUB_MAXINE_LIPSYNC_LANGUAGE";

    public static NvidiaMaxineLipSyncOptions FromEnvironment() =>
        new(
            Environment.GetEnvironmentVariable(HostExecutableEnvironmentVariable),
            Environment.GetEnvironmentVariable(ArSdkRootEnvironmentVariable),
            NvidiaMaxineLipSyncLanguages.Parse(Environment.GetEnvironmentVariable(LanguageEnvironmentVariable))
                ?? NvidiaMaxineLipSyncLanguage.Generic);

    public string? ModelDirectory =>
        string.IsNullOrWhiteSpace(ArSdkRoot) ? null : Path.Join(ArSdkRoot, "bin", "models");
}

/// <summary>LipSync model selection; values match the AR SDK sample host's <c>--language</c> flag.</summary>
public enum NvidiaMaxineLipSyncLanguage
{
    Generic = 0,
    German = 1,
    French = 2,
    Spanish = 3,
}

public static class NvidiaMaxineLipSyncLanguages
{
    /// <summary>Accepts an ISO 639-1 code, an English name, or the numeric flag value.</summary>
    public static NvidiaMaxineLipSyncLanguage? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "0" or "generic" or "auto" => NvidiaMaxineLipSyncLanguage.Generic,
            "1" or "de" or "german" => NvidiaMaxineLipSyncLanguage.German,
            "2" or "fr" or "french" => NvidiaMaxineLipSyncLanguage.French,
            "3" or "es" or "spanish" => NvidiaMaxineLipSyncLanguage.Spanish,
            _ => null,
        };
    }

    /// <summary>
    /// Reads a language suffix from a model alias such as <c>nvidia-maxine-lipsync-es</c>.
    /// Returns null when the alias does not name a language.
    /// </summary>
    public static NvidiaMaxineLipSyncLanguage? FromModelAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias) ||
            !alias.StartsWith(NvidiaMaxineLipSynthesisEngine.ModelAliasPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string suffix = alias[NvidiaMaxineLipSynthesisEngine.ModelAliasPrefix.Length..].TrimStart('-', ':');
        return Parse(suffix);
    }
}
