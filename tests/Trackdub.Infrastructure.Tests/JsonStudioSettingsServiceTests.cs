using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Cloud;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class JsonStudioSettingsServiceTests
{
    [Fact]
    public async Task Save_and_load_round_trip_all_fields()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var service = new JsonStudioSettingsService(storagePaths);
        var settings = new StudioSettings(
            DefaultSourceLanguage: "es",
            DefaultTargetLanguage: "en",
            ModelTierPreference: "quality",
            WindowLayout: new WindowLayoutSettings(1600, 900, IsMaximized: true),
            RecentProjects:
            [
                new RecentProjectEntry("One", @"D:\Projects\One.trackdub", DateTimeOffset.UtcNow)
            ],
            TtsTiming: new TtsTimingSettings(
                EnableRubberbandStretch: true,
                RubberbandStretchThreshold: 0.25d,
                AutoStretchMaxOverrun: 0.4d),
            TranscriptConfidenceThreshold: 0.63d,
            AsrModelOverride: AsrModelOverride.OnnxRuntime,
            Export: new StudioExportSettings(
                Srt: false,
                Vtt: true,
                Ass: true,
                BurnInSubtitles: true,
                TargetLufs: -23d,
                Container: "mkv",
                SubtitleSource: "transcript",
                MatchOriginalLoudness: true),
            Playback: new StudioPlaybackSettings(
                SubtitlesEnabled: true,
                SubtitleContentMode: StudioPlaybackSettings.BilingualSubtitleContentMode,
                KaraokeEnabled: true),
            StarterPackOnboardingCompleted: true);

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal("es", loaded.DefaultSourceLanguage);
            Assert.Equal("en", loaded.DefaultTargetLanguage);
            Assert.Equal("quality", loaded.ModelTierPreference);
            Assert.Equal(1600, loaded.WindowLayout.Width);
            Assert.Equal(900, loaded.WindowLayout.Height);
            Assert.True(loaded.WindowLayout.IsMaximized);
            Assert.Single(loaded.RecentProjects);
            Assert.True(loaded.TtsTiming!.EnableRubberbandStretch);
            Assert.Equal(0.25d, loaded.TtsTiming.RubberbandStretchThreshold);
            Assert.Equal(0.4d, loaded.TtsTiming.AutoStretchMaxOverrun);
            Assert.Equal(0.63d, loaded.TranscriptConfidenceThreshold);
            Assert.Equal(AsrModelOverride.OnnxRuntime, loaded.AsrModelOverride);
            Assert.NotNull(loaded.Export);
            Assert.False(loaded.Export!.Srt);
            Assert.True(loaded.Export.Vtt);
            Assert.True(loaded.Export.Ass);
            Assert.True(loaded.Export.BurnInSubtitles);
            Assert.Equal(-23d, loaded.Export.TargetLufs);
            Assert.True(loaded.Export.MatchOriginalLoudness);
            Assert.Equal("mkv", loaded.Export.Container);
            Assert.Equal("transcript", loaded.Export.SubtitleSource);
            Assert.NotNull(loaded.Playback);
            Assert.True(loaded.Playback!.SubtitlesEnabled);
            Assert.Equal(StudioPlaybackSettings.BilingualSubtitleContentMode, loaded.Playback.SubtitleContentMode);
            Assert.True(loaded.Playback.KaraokeEnabled);
            Assert.True(loaded.StarterPackOnboardingCompleted);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(false, false)]
    public async Task WarmModelsAtStartup_defaults_on_for_settings_saved_without_it_and_round_trips(
        bool? saved,
        bool expected)
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            if (saved is bool value)
            {
                await service.SaveAsync(
                    StudioSettings.Default with { WarmModelsAtStartup = value },
                    TestContext.Current.CancellationToken);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storagePaths.SettingsPath)!);
                await File.WriteAllTextAsync(
                    storagePaths.SettingsPath,
                    """
                    {
                      "defaultSourceLanguage": "es",
                      "defaultTargetLanguage": "fr",
                      "modelTierPreference": "quality",
                      "windowLayout": { "width": 1280, "height": 720, "isMaximized": false },
                      "recentProjects": []
                    }
                    """,
                    TestContext.Current.CancellationToken);
            }

            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(expected, loaded.WarmModelsAtStartup);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_unknown_asr_model_override_keeps_other_settings()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storagePaths.SettingsPath)!);
            await File.WriteAllTextAsync(
                storagePaths.SettingsPath,
                """
                {
                  "defaultSourceLanguage": "es",
                  "defaultTargetLanguage": "fr",
                  "modelTierPreference": "quality",
                  "commercialSafeMode": false,
                  "windowLayout": {
                    "width": 1280,
                    "height": 720,
                    "isMaximized": false
                  },
                  "recentProjects": [],
                  "transcriptConfidenceThreshold": 0.62,
                  "asrModelOverride": "future-provider"
                }
                """);

            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal("es", loaded.DefaultSourceLanguage);
            Assert.Equal("fr", loaded.DefaultTargetLanguage);
            Assert.Equal("quality", loaded.ModelTierPreference);
            Assert.Equal(1280, loaded.WindowLayout.Width);
            Assert.Equal(720, loaded.WindowLayout.Height);
            Assert.Equal(0.62d, loaded.TranscriptConfidenceThreshold);
            Assert.Equal(AsrModelOverride.Auto, loaded.AsrModelOverride);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_nemotron_asr_model_override_from_key()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storagePaths.SettingsPath)!);
            await File.WriteAllTextAsync(
                storagePaths.SettingsPath,
                """
                {
                  "defaultSourceLanguage": "en",
                  "defaultTargetLanguage": "es",
                  "modelTierPreference": "quality",
                  "commercialSafeMode": false,
                  "windowLayout": {
                    "width": 1280,
                    "height": 720,
                    "isMaximized": false
                  },
                  "recentProjects": [],
                  "transcriptConfidenceThreshold": 0.62,
                  "asrModelOverride": "nemotron-3.5"
                }
                """);

            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(AsrModelOverride.Nemotron35, loaded.AsrModelOverride);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_missing_playback_settings_uses_subtitles_off_default()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(storagePaths.SettingsPath)!);
            await File.WriteAllTextAsync(
                storagePaths.SettingsPath,
                """
                {
                  "defaultSourceLanguage": "es",
                  "defaultTargetLanguage": "fr",
                  "modelTierPreference": "quality",
                  "commercialSafeMode": false,
                  "windowLayout": {
                    "width": 1280,
                    "height": 720,
                    "isMaximized": false
                  },
                  "recentProjects": []
                }
                """);

            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(loaded.Playback);
            Assert.False(loaded.Playback!.SubtitlesEnabled);
            Assert.Equal(StudioPlaybackSettings.TranslatedSubtitleContentMode, loaded.Playback.SubtitleContentMode);
            Assert.False(loaded.Playback.KaraokeEnabled);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_normalizes_invalid_subtitle_content_mode()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with
        {
            Playback = new StudioPlaybackSettings(
                SubtitlesEnabled: true,
                SubtitleContentMode: "future-mode")
        };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(loaded.Playback);
            Assert.False(loaded.Playback!.SubtitlesEnabled);
            Assert.Equal(StudioPlaybackSettings.TranslatedSubtitleContentMode, loaded.Playback.SubtitleContentMode);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_preserves_bilingual_export_subtitle_source()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with
        {
            Export = StudioExportSettings.Default with
            {
                SubtitleSource = StudioExportSettings.BilingualSubtitleSource
            }
        };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(loaded.Export);
            Assert.Equal(StudioExportSettings.BilingualSubtitleSource, loaded.Export!.SubtitleSource);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TouchRecentProjectAsync_keeps_only_ten_entries_ordered_newest_first()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));

        try
        {
            for (int index = 0; index < 11; index++)
            {
                await service.TouchRecentProjectAsync(
                    $@"D:\Projects\Project{index}.trackdub",
                    $"Project {index}",
                    TestContext.Current.CancellationToken);
            }

            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(10, loaded.RecentProjects.Count);
            Assert.DoesNotContain(loaded.RecentProjects, entry => entry.ProjectName == "Project 0");
            Assert.Equal("Project 10", loaded.RecentProjects[0].ProjectName);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_defaults_rubberband_stretch_off()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));

        try
        {
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(loaded.TtsTiming);
            Assert.False(loaded.TtsTiming!.EnableRubberbandStretch);
            Assert.Equal(0.15d, loaded.TtsTiming.RubberbandStretchThreshold);
            Assert.Null(loaded.TtsTiming.AutoStretchMaxOverrun);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_normalizes_invalid_transcript_confidence_threshold()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with { TranscriptConfidenceThreshold = 1.5d };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(StudioSettings.DefaultTranscriptConfidenceThreshold, loaded.TranscriptConfidenceThreshold);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_defaults_windows_ml_device_policy_to_explicit_when_property_absent()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        string settingsPath = storagePaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(
            settingsPath,
            """{"commercialSafeMode":true,"modelTierPreference":"balanced","windowLayout":{"width":null,"height":null,"isMaximized":false},"recentProjects":[]}""",
            TestContext.Current.CancellationToken);

        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WindowsMlExecutionDevicePolicy.Explicit, loaded.WindowsMlExecutionDevicePolicy);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_round_trips_windows_ml_device_policy()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with
        {
            WindowsMlExecutionDevicePolicy = WindowsMlExecutionDevicePolicy.MaxPerformance
        };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WindowsMlExecutionDevicePolicy.MaxPerformance, loaded.WindowsMlExecutionDevicePolicy);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_normalizes_model_variant_override_keys()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with
        {
            ModelVariantOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [" Translation : MADLAD400-3B-MT "] = "olive-cpu-int8",
                ["translation"] = "olive-cpu-fp32"
            }
        };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                "olive-cpu-int8",
                loaded.ModelVariantOverrides![ModelVariantOverrideKeys.Build("translation", "madlad400-3b-mt")]);
            Assert.Equal("olive-cpu-fp32", loaded.ModelVariantOverrides["translation"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_archives_corrupt_json_and_returns_defaults()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        string settingsPath = storagePaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(settingsPath, "{not-json", TestContext.Current.CancellationToken);

        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WindowsMlExecutionDevicePolicy.Explicit, loaded.WindowsMlExecutionDevicePolicy);
            Assert.False(File.Exists(settingsPath));
            Assert.True(Directory.GetFiles(Path.GetDirectoryName(settingsPath)!, "settings.json.*.corrupt").Length >= 1);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_legacy_json_without_starter_pack_fields_uses_defaults()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        string settingsPath = storagePaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(
            settingsPath,
            """{"commercialSafeMode":true,"modelTierPreference":"balanced","windowLayout":{"width":null,"height":null,"isMaximized":false},"recentProjects":[]}""",
            TestContext.Current.CancellationToken);

        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(loaded.StageModelAliases);
            Assert.Empty(loaded.StageModelAliases);
            Assert.Null(loaded.AppliedStarterPackId);
            Assert.Null(loaded.AppliedStarterPackProfileId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_round_trips_cloud_model_overrides()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with
        {
            AsrModelOverride = AsrModelOverride.OpenAiWhisper,
            TranslationModelOverride = TranslationModelOverride.OpenAiGpt,
            TtsModelOverride = TtsModelOverride.OpenAiTts,
            StageModelAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["asr"] = AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
                ["translation"] = TranslationModelOverrideSettings.OpenAiGptCloudAlias,
                ["tts"] = TtsModelOverrideSettings.OpenAiTtsCloudAlias,
            },
            AppliedStarterPackId = "cloud",
            AppliedStarterPackProfileId = "default",
        };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(AsrModelOverride.OpenAiWhisper, loaded.AsrModelOverride);
            Assert.Equal(TranslationModelOverride.OpenAiGpt, loaded.TranslationModelOverride);
            Assert.Equal(TtsModelOverride.OpenAiTts, loaded.TtsModelOverride);
            Assert.Equal("cloud", loaded.AppliedStarterPackId);
            Assert.Equal(
                AsrModelOverrideSettings.OpenAiWhisperCloudAlias,
                loaded.StageModelAliases!["asr"]);
            Assert.Equal(
                TranslationModelOverrideSettings.OpenAiGptCloudAlias,
                loaded.StageModelAliases!["translation"]);
            Assert.Equal(
                TtsModelOverrideSettings.OpenAiTtsCloudAlias,
                loaded.StageModelAliases!["tts"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Save_and_load_round_trips_cloud_egress_consents()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var service = new JsonStudioSettingsService(new TrackdubStoragePaths(tempRoot));
        var settings = StudioSettings.Default with
        {
            CloudEgressConsents = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
            {
                [" AUDIO:GEMINI "] = true,
                [CloudEgressConsentKeys.TextGemini] = false,
                [CloudEgressConsentKeys.MediaElevenLabs] = true,
            }
        };

        try
        {
            await service.SaveAsync(settings, TestContext.Current.CancellationToken);
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(loaded.CloudEgressConsents);
            Assert.True(loaded.CloudEgressConsents![CloudEgressConsentKeys.AudioGemini]);
            Assert.False(loaded.CloudEgressConsents[CloudEgressConsentKeys.TextGemini]);
            Assert.True(loaded.CloudEgressConsents[CloudEgressConsentKeys.MediaElevenLabs]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Load_parses_string_windows_ml_device_policy()
    {
        string tempRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        string settingsPath = storagePaths.SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        await File.WriteAllTextAsync(
            settingsPath,
            """{"commercialSafeMode":true,"modelTierPreference":"balanced","windowLayout":{"width":null,"height":null,"isMaximized":false},"recentProjects":[],"windowsMlExecutionDevicePolicy":"max-performance"}""",
            TestContext.Current.CancellationToken);

        var service = new JsonStudioSettingsService(storagePaths);

        try
        {
            StudioSettings loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(WindowsMlExecutionDevicePolicy.MaxPerformance, loaded.WindowsMlExecutionDevicePolicy);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
