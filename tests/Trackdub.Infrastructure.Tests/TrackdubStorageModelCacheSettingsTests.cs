using System.Text.Json;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class TrackdubStorageModelCacheSettingsTests
{
    [Fact]
    public void Resolve_uses_saved_model_cache_directory()
    {
        string root = CreateTestRoot();
        string localAppData = Path.Join(root, "local-app-data");
        string userData = Path.Join(localAppData, TrackdubStoragePathResolver.ProductDirectoryName);
        string configuredModelCache = Path.Join(root, "large-drive", "models");
        Directory.CreateDirectory(userData);
        File.WriteAllText(
            Path.Join(userData, "settings.json"),
            JsonSerializer.Serialize(new { defaultModelCacheDirectory = configuredModelCache }));

        var context = new TrackdubStoragePathResolutionContext(
            Path.Join(root, "app"),
            localAppData,
            Path.Join(root, "common-app-data"),
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);
        var storagePaths = new TrackdubStoragePaths(options);

        Assert.Equal(Path.GetFullPath(configuredModelCache), options.ExplicitModelCacheDirectory);
        Assert.Equal(Path.GetFullPath(configuredModelCache), storagePaths.ModelCacheDirectory);
        Assert.Equal(Path.Join(Path.GetFullPath(configuredModelCache), "model-cache-records.json"), storagePaths.ModelCacheIndexPath);
    }

    [Fact]
    public void Resolve_ignores_invalid_settings_json_and_uses_default_model_cache_directory()
    {
        string root = CreateTestRoot();
        string localAppData = Path.Join(root, "local-app-data");
        string userData = Path.Join(localAppData, TrackdubStoragePathResolver.ProductDirectoryName);
        Directory.CreateDirectory(userData);
        File.WriteAllText(Path.Join(userData, "settings.json"), "{ invalid json");

        var context = new TrackdubStoragePathResolutionContext(
            Path.Join(root, "app"),
            localAppData,
            Path.Join(root, "common-app-data"),
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);
        var storagePaths = new TrackdubStoragePaths(options);

        Assert.Null(options.ExplicitModelCacheDirectory);
        Assert.Equal(Path.Join(Path.GetFullPath(userData), "model-cache"), storagePaths.ModelCacheDirectory);
    }

    private static string CreateTestRoot()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.StorageModelCacheSettings.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
