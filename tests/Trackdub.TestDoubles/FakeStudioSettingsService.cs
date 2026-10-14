using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeStudioSettingsService : IStudioSettingsService
{
    private const int RecentProjectLimit = 10;

    public StudioSettings CurrentSettings { get; private set; } = StudioSettings.Default;

    public Exception? LoadException { get; set; }

    public Task<StudioSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<StudioSettings>(cancellationToken);
        }

        if (LoadException is not null)
        {
            return Task.FromException<StudioSettings>(LoadException);
        }

        return Task.FromResult(CurrentSettings);
    }

    public Task SaveAsync(StudioSettings settings, CancellationToken cancellationToken)
    {
        CurrentSettings = Normalize(settings);
        return Task.CompletedTask;
    }

    public Task<StudioSettings> TouchRecentProjectAsync(
        string projectPath,
        string projectName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        string normalizedPath = Path.GetFullPath(projectPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RecentProjectEntry entry = new(projectName.Trim(), normalizedPath, now);
        RecentProjectEntry[] updatedRecentProjects =
            [entry, .. CurrentSettings.RecentProjects
                .Where(candidate => !string.Equals(candidate.ProjectPath, normalizedPath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(candidate => candidate.LastOpenedAtUtc)
                .Take(RecentProjectLimit - 1)];

        CurrentSettings = Normalize(CurrentSettings with { RecentProjects = updatedRecentProjects });
        return Task.FromResult(CurrentSettings);
    }

    private static StudioSettings Normalize(StudioSettings settings)
    {
        RecentProjectEntry[] recentProjects = settings.RecentProjects
            .Where(entry => !string.IsNullOrWhiteSpace(entry.ProjectPath) && !string.IsNullOrWhiteSpace(entry.ProjectName))
            .Select(entry => new RecentProjectEntry(
                entry.ProjectName.Trim(),
                Path.GetFullPath(entry.ProjectPath),
                entry.LastOpenedAtUtc))
            .OrderByDescending(entry => entry.LastOpenedAtUtc)
            .DistinctBy(entry => entry.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Take(RecentProjectLimit)
            .ToArray();

        return settings with
        {
            DefaultSourceLanguage = NormalizeLanguageCode(settings.DefaultSourceLanguage) ?? StudioSettings.Default.DefaultSourceLanguage,
            DefaultTargetLanguage = NormalizeLanguageCode(settings.DefaultTargetLanguage) ?? StudioSettings.Default.DefaultTargetLanguage,
            ModelTierPreference = string.IsNullOrWhiteSpace(settings.ModelTierPreference)
                ? StudioSettings.Default.ModelTierPreference
                : settings.ModelTierPreference.Trim().ToLowerInvariant(),
            WindowLayout = settings.WindowLayout ?? StudioSettings.Default.WindowLayout,
            RecentProjects = recentProjects,
            TtsTiming = NormalizeTtsTiming(settings.TtsTiming),
            TranscriptConfidenceThreshold = NormalizeConfidenceThreshold(settings.TranscriptConfidenceThreshold),
            AsrModelOverride = NormalizeAsrModelOverride(settings.AsrModelOverride),
            Export = NormalizeExportSettings(settings.Export),
            CloudEgressConsents = NormalizeCloudEgressConsents(settings.CloudEgressConsents)
        };
    }

    private static IReadOnlyDictionary<string, bool> NormalizeCloudEgressConsents(
        IReadOnlyDictionary<string, bool>? consents)
    {
        if (consents is null || consents.Count == 0)
        {
            return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, bool value) in consents)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                normalized[key.Trim().ToLowerInvariant()] = value;
            }
        }

        return normalized;
    }

    private static TtsTimingSettings NormalizeTtsTiming(TtsTimingSettings? settings)
    {
        settings ??= TtsTimingSettings.Default;
        double threshold = double.IsFinite(settings.RubberbandStretchThreshold) &&
                           settings.RubberbandStretchThreshold is >= 0d and <= 1d
            ? settings.RubberbandStretchThreshold
            : TtsTimingSettings.Default.RubberbandStretchThreshold;
        return settings with { RubberbandStretchThreshold = threshold };
    }

    private static double NormalizeConfidenceThreshold(double threshold) =>
        double.IsFinite(threshold) && threshold is >= 0d and <= 1d
            ? threshold
            : StudioSettings.DefaultTranscriptConfidenceThreshold;

    private static AsrModelOverride NormalizeAsrModelOverride(AsrModelOverride modelOverride) =>
        modelOverride is AsrModelOverride.Auto
            or AsrModelOverride.GenAi
            or AsrModelOverride.OnnxRuntime
            or AsrModelOverride.Nemotron35
            or AsrModelOverride.OpenAiWhisper
            or AsrModelOverride.GeminiAsr
            ? modelOverride
            : StudioSettings.Default.AsrModelOverride;

    private static StudioExportSettings NormalizeExportSettings(StudioExportSettings? settings)
    {
        settings ??= StudioExportSettings.Default;
        string container = string.Equals(settings.Container, StudioExportSettings.MkvContainer, StringComparison.OrdinalIgnoreCase)
            ? StudioExportSettings.MkvContainer
            : StudioExportSettings.Mp4Container;
        string subtitleSource = settings.SubtitleSource?.Trim().ToLowerInvariant() switch
        {
            StudioExportSettings.TranscriptSubtitleSource => StudioExportSettings.TranscriptSubtitleSource,
            StudioExportSettings.BilingualSubtitleSource => StudioExportSettings.BilingualSubtitleSource,
            _ => StudioExportSettings.TranslatedSubtitleSource
        };
        return settings with
        {
            TargetLufs = ExportLoudnessTargets.NormalizeTargetLufs(settings.TargetLufs),
            Container = container,
            SubtitleSource = subtitleSource
        };
    }

    private static string? NormalizeLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();
}
