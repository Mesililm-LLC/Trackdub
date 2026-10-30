using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class LocalModelCacheRecordStoreMigrationTests : IDisposable
{
    private readonly string root = Path.Join(
        Path.GetTempPath(),
        "Trackdub.LocalModelCacheRecordStoreMigration.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Copy_records_to_new_directory_merges_indexes_and_keeps_source_intact()
    {
        TrackdubStoragePaths sourcePaths = CreateStoragePaths(Path.Join(root, "old-cache"));
        TrackdubStoragePaths destinationPaths = CreateStoragePaths(Path.Join(root, "new-cache"));
        var sourceStore = new LocalModelCacheRecordStore(sourcePaths);
        var destinationStore = new LocalModelCacheRecordStore(destinationPaths);
        var existingRecord = new LocalModelCacheRecord(
            "test/source-model",
            Path.Join(root, "old-cache", "test", "source-model"),
            "source-revision",
            new string('a', 64),
            DateTimeOffset.UnixEpoch);
        var destinationRecord = new LocalModelCacheRecord(
            "test/destination-model",
            Path.Join(root, "new-cache", "test", "destination-model"),
            "destination-revision",
            new string('b', 64),
            DateTimeOffset.UnixEpoch.AddDays(1));
        await sourceStore.MutateAsync(_ => [existingRecord], TestContext.Current.CancellationToken);
        await destinationStore.MutateAsync(_ => [destinationRecord], TestContext.Current.CancellationToken);

        await sourceStore.CopyRecordsToDirectoryAsync(
            new DirectoryInfo(destinationPaths.ModelCacheDirectory),
            TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> copiedRecords = await destinationStore.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, copiedRecords.Count);
        Assert.Contains(copiedRecords, record => record.ModelId == existingRecord.ModelId && record.RootPath == existingRecord.RootPath);
        Assert.Contains(copiedRecords, record => record.ModelId == destinationRecord.ModelId && record.RootPath == destinationRecord.RootPath);
        Assert.True(File.Exists(sourcePaths.ModelCacheIndexPath));
        Assert.False(Directory.Exists(existingRecord.RootPath));

        var downloadedAfterChange = new LocalModelCacheRecord(
            "test/next-download",
            Path.Join(root, "old-cache", "test", "next-download"),
            "new-revision",
            new string('c', 64),
            DateTimeOffset.UnixEpoch.AddDays(2));
        await sourceStore.MutateAsync(
            records => [.. records, downloadedAfterChange],
            TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> updatedDestination = await destinationStore.LoadAsync(TestContext.Current.CancellationToken);
        Assert.Contains(updatedDestination, record => record.ModelId == downloadedAfterChange.ModelId);
    }

    [Fact]
    public async Task Copy_empty_index_to_new_directory_mirrors_records_added_before_restart()
    {
        TrackdubStoragePaths sourcePaths = CreateStoragePaths(Path.Join(root, "empty-old-cache"));
        TrackdubStoragePaths destinationPaths = CreateStoragePaths(Path.Join(root, "empty-new-cache"));
        var sourceStore = new LocalModelCacheRecordStore(sourcePaths);
        var destinationStore = new LocalModelCacheRecordStore(destinationPaths);

        await sourceStore.CopyRecordsToDirectoryAsync(
            new DirectoryInfo(destinationPaths.ModelCacheDirectory),
            TestContext.Current.CancellationToken);

        var addedBeforeRestart = new LocalModelCacheRecord(
            "test/downloaded-before-restart",
            Path.Join(root, "empty-old-cache", "test", "downloaded-before-restart"),
            "revision",
            new string('d', 64),
            DateTimeOffset.UnixEpoch);
        await sourceStore.MutateAsync(
            _ => [addedBeforeRestart],
            TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> destinationRecords = await destinationStore.LoadAsync(
            TestContext.Current.CancellationToken);
        Assert.Contains(destinationRecords, record => record.ModelId == addedBeforeRestart.ModelId);
    }

    private TrackdubStoragePaths CreateStoragePaths(string modelCacheDirectory) => new(new TrackdubStorageOptions(
        Path.Join(root, "user-data"),
        Path.Join(root, "user-cache"),
        SharedAssetRoot: null,
        IsPortable: false,
        ExplicitModelCacheDirectory: modelCacheDirectory));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
