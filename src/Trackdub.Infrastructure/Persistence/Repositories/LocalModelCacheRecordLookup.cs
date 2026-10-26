using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRecordLookup(LocalModelCacheRecordStore recordStore) : IModelCacheRecordLookup
{
    private readonly LocalModelCacheRecordStore recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));
    private readonly object cacheGate = new();
    private IReadOnlyList<LocalModelCacheRecord>? cachedRecords;
    private int cachedRevision = -1;
    private (long LastWriteTicks, long Length)? cachedIdentity;

    public LocalModelCacheRecord? Find(string modelId, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string normalizedRootPath = Path.GetFullPath(rootPath);
        IReadOnlyList<LocalModelCacheRecord> records;
        lock (cacheGate)
        {
            int currentRevision = recordStore.Revision;
            var currentIdentity = recordStore.GetIndexIdentity();
            if (cachedRecords is null || cachedRevision != currentRevision || cachedIdentity != currentIdentity)
            {
                cachedRecords = recordStore.Load();
                cachedRevision = currentRevision;
                cachedIdentity = currentIdentity;
            }

            records = cachedRecords;
        }

        return records.FirstOrDefault(candidate =>
            string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
            LocalModelCacheRecordStore.RootsEqual(candidate.RootPath, normalizedRootPath) &&
            !candidate.IntegrityFailed);
    }
}
