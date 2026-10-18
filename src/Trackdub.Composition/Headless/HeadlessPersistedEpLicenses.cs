using Trackdub.Contracts;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Composition.Headless;

/// <summary>
/// Reads and writes vendor EP license flags on disk. Headless CLI hosts wrap
/// <see cref="IStudioSettingsService"/> with an in-memory overlay so process-local
/// hardware pins do not mutate studio settings; license acceptance still has to
/// survive to the next process.
/// </summary>
public static class HeadlessPersistedEpLicenses
{
    public static TrackdubStoragePaths ResolveStoragePaths(HeadlessTrackdubOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ModelDirectory is null &&
            options.ModelCacheDirectory is null &&
            options.LogDirectory is null)
        {
            return new TrackdubStoragePaths();
        }

        string userDataRoot = options.LogDirectory
            ?? options.ModelDirectory
            ?? options.ModelCacheDirectory!;
        string userCacheRoot = options.ModelDirectory
            ?? options.ModelCacheDirectory
            ?? options.LogDirectory!;

        return new TrackdubStoragePaths(
            new TrackdubStorageOptions(
                UserDataRoot: userDataRoot,
                UserCacheRoot: userCacheRoot,
                SharedAssetRoot: null,
                IsPortable: false,
                ExplicitModelCacheDirectory: options.ModelCacheDirectory ?? options.ModelDirectory,
                ExplicitEngineCacheDirectory: Environment.GetEnvironmentVariable(
                    TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable)));
    }

    public static StudioSettings Load(HeadlessTrackdubOptions options) =>
        Load(ResolveStoragePaths(options));

    public static StudioSettings Load(IAppStoragePaths storagePaths)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        JsonStudioSettingsService disk = CreateDiskStore(storagePaths);
        return disk.LoadAsync(CancellationToken.None).ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public static async Task PersistNvidiaTensorRtRtxAcceptanceAsync(
        IAppStoragePaths storagePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        JsonStudioSettingsService disk = CreateDiskStore(storagePaths);
        StudioSettings current = await disk.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.NvidiaTensorRtRtxLicenseAccepted)
        {
            return;
        }

        await disk.SaveAsync(
                current with { NvidiaTensorRtRtxLicenseAccepted = true },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task PersistQualcommQnnAcceptanceAsync(
        IAppStoragePaths storagePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        JsonStudioSettingsService disk = CreateDiskStore(storagePaths);
        StudioSettings current = await disk.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current.QualcommQnnLicenseAccepted)
        {
            return;
        }

        await disk.SaveAsync(
                current with { QualcommQnnLicenseAccepted = true },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static JsonStudioSettingsService CreateDiskStore(IAppStoragePaths storagePaths) =>
        new(storagePaths);
}
