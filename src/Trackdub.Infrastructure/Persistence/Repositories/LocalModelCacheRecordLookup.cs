using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRecordLookup(LocalModelCacheRecordStore recordStore) : IModelCacheRecordLookup
{
    private readonly LocalModelCacheRecordStore recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
    private readonly object cacheGate = new();
    private IReadOnlyList<LocalModelCacheRecord>? cachedRecords;
    private int cachedRevision = -1;

    public LocalModelCacheRecord? Find(string modelId, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string normalizedRootPath = Path.GetFullPath(rootPath);
        IReadOnlyList<LocalModelCacheRecord> records;
        lock (cacheGate)
        {
            int currentRevision = recordStore.Revision;
            if (cachedRecords is null || cachedRevision != currentRevision)
            {
                cachedRecords = recordStore.Load();
                cachedRevision = currentRevision;
            }

            records = cachedRecords;
        }

        return records.FirstOrDefault(candidate =>
            string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
            LocalModelCacheRecordStore.RootsEqual(candidate.RootPath, normalizedRootPath) &&
            !candidate.IntegrityFailed);
    }
}
