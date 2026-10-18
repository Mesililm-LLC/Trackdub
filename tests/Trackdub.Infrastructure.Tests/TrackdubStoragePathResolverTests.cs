using System.Text;
using System.Text.Json;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class TrackdubStoragePathResolverTests
{
    [Fact]
    public void Resolve_uses_portable_data_root_next_to_app_when_marker_exists()
    {
        string testRoot = CreateTestRoot();
        string appBaseDirectory = Path.Join(testRoot, "app");
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Join(appBaseDirectory, "Trackdub.portable"), string.Empty);

        var context = new TrackdubStoragePathResolutionContext(
            appBaseDirectory,
            Path.Join(testRoot, "local"),
            Path.Join(testRoot, "program-data"),
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);
        var paths = new TrackdubStoragePaths(options);

        string portableRoot = Path.GetFullPath(Path.Join(appBaseDirectory, "portable-data"));
        Assert.True(options.IsPortable);
        Assert.Equal(portableRoot, options.UserDataRoot);
        Assert.Equal(portableRoot, options.UserCacheRoot);
        Assert.Null(options.SharedAssetRoot);
        Assert.Equal(Path.Join(portableRoot, "model-cache"), paths.ModelCacheDirectory);
        Assert.Equal(Path.Join(portableRoot, "avalonia-layout.json"), paths.LayoutPath);
        Assert.Equal(Path.Join(portableRoot, "tools", "ffmpeg"), paths.FfmpegToolCacheDirectory);
        Assert.Equal(Path.Join(portableRoot, "EngineCache"), paths.EngineCacheDirectory);
    }

    [Fact]
    public void Resolve_prefers_environment_roots_over_portable_marker()
    {
        string testRoot = CreateTestRoot();
        string appBaseDirectory = Path.Join(testRoot, "app");
        Directory.CreateDirectory(appBaseDirectory);
        File.WriteAllText(Path.Join(appBaseDirectory, "Trackdub.portable"), string.Empty);

        string dataRoot = Path.Join(testRoot, "env-data");
        string cacheRoot = Path.Join(testRoot, "env-cache");
        string sharedRoot = Path.Join(testRoot, "env-shared");
        var environment = new Dictionary<string, string?>
        {
            ["TRACKDUB_DATA_ROOT"] = dataRoot,
            ["TRACKDUB_CACHE_ROOT"] = cacheRoot,
            ["TRACKDUB_SHARED_ASSET_ROOT"] = sharedRoot
        };

        var context = new TrackdubStoragePathResolutionContext(
            appBaseDirectory,
            Path.Join(testRoot, "local"),
            Path.Join(testRoot, "program-data"),
            environment);

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);

        Assert.False(options.IsPortable);
        Assert.Equal(Path.GetFullPath(dataRoot), options.UserDataRoot);
        Assert.Equal(Path.GetFullPath(cacheRoot), options.UserCacheRoot);
        Assert.Equal(Path.GetFullPath(sharedRoot), options.SharedAssetRoot);
    }

    [Fact]
    public void Resolve_uses_installer_storage_config_from_common_app_data()
    {
        string testRoot = CreateTestRoot();
        string appBaseDirectory = Path.Join(testRoot, "app");
        string localRoot = Path.Join(testRoot, "local");
        string commonRoot = Path.Join(testRoot, "program-data");
        string dataRoot = Path.Join(testRoot, "configured-data");
        string cacheRoot = Path.Join(testRoot, "configured-cache");
        string sharedRoot = Path.Join(testRoot, "configured-shared");
        string configDirectory = Path.Join(commonRoot, "Trackdub");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(
            Path.Join(configDirectory, "storage.json"),
            JsonSerializer.Serialize(new
            {
                userDataRoot = dataRoot,
                userCacheRoot = cacheRoot,
                sharedAssetRoot = sharedRoot
            }));

        var context = new TrackdubStoragePathResolutionContext(
            appBaseDirectory,
            localRoot,
            commonRoot,
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);

        Assert.False(options.IsPortable);
        Assert.Equal(Path.GetFullPath(dataRoot), options.UserDataRoot);
        Assert.Equal(Path.GetFullPath(cacheRoot), options.UserCacheRoot);
        Assert.Equal(Path.GetFullPath(sharedRoot), options.SharedAssetRoot);
    }

    // The installer config is read through a FileStream: a storage.json re-saved by a Windows editor
    // (UTF-8 byte order mark, CRLF endings) has to resolve the same roots.
    [Fact]
    public void Resolve_reads_installer_storage_config_re_saved_with_a_utf8_bom_and_windows_newlines()
    {
        string testRoot = CreateTestRoot();
        string commonRoot = Path.Join(testRoot, "program-data");
        string dataRoot = Path.Join(testRoot, "configured-data");
        string cacheRoot = Path.Join(testRoot, "configured-cache");
        string configDirectory = Path.Join(commonRoot, "Trackdub");
        Directory.CreateDirectory(configDirectory);
        string json = JsonSerializer.Serialize(
            new
            {
                userDataRoot = dataRoot,
                userCacheRoot = cacheRoot
            },
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllBytes(
            Path.Join(configDirectory, "storage.json"),
            [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(json.Replace("\n", "\r\n", StringComparison.Ordinal))]);

        var context = new TrackdubStoragePathResolutionContext(
            Path.Join(testRoot, "app"),
            Path.Join(testRoot, "local"),
            commonRoot,
            new Dictionary<string, string?>());

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);

        Assert.False(options.IsPortable);
        Assert.Equal(Path.GetFullPath(dataRoot), options.UserDataRoot);
        Assert.Equal(Path.GetFullPath(cacheRoot), options.UserCacheRoot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Resolve_preserves_normalized_engine_cache_override(bool portable)
    {
        string testRoot = CreateTestRoot();
        string engineRoot = Path.Join(testRoot, "cache", "..", "mounted-engines");
        var environment = new Dictionary<string, string?>
        {
            [TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable] = engineRoot,
            [TrackdubStoragePathResolver.PortableEnvironmentVariable] = portable ? "1" : null,
        };
        var context = new TrackdubStoragePathResolutionContext(
            Path.Join(testRoot, "app"), Path.Join(testRoot, "local"), Path.Join(testRoot, "shared"), environment);

        TrackdubStorageOptions options = TrackdubStoragePathResolver.Resolve(context);
        var storagePaths = new TrackdubStoragePaths(options);

        Assert.Equal(portable, options.IsPortable);
        Assert.Equal(Path.GetFullPath(engineRoot), options.ExplicitEngineCacheDirectory);
        Assert.Equal(Path.GetFullPath(engineRoot), storagePaths.EngineCacheDirectory);
        Assert.NotEqual(Path.Join(storagePaths.UserCacheRoot, "EngineCache"), storagePaths.EngineCacheDirectory);
    }

    private static string CreateTestRoot()
    {
        string root = Path.Join(
            Path.GetTempPath(),
            "Trackdub.StoragePathResolver.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
