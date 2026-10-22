using Trackdub.Cloud.Jobs;

namespace Trackdub.TestDoubles;

/// <summary>Local test authority; never registered by application hosts.</summary>
public sealed class FakeJobStore : IJobStore
{
    private readonly object _gate = new();
    public Dictionary<Guid, JobRecord> Jobs { get; } = [];
    public Dictionary<Guid, UploadRecord> Uploads { get; } = [];
    public Dictionary<(string Owner, string Key), (Guid Id, string Hash)> Idempotency { get; } = [];
    public HashSet<Guid> Outbox { get; } = [];
    public bool FailAcceptance { get; set; }
    public Task<JobRecord?> GetAsync(Guid id, CancellationToken token) { lock (_gate) return Task.FromResult(Jobs.GetValueOrDefault(id)); }
    public Task<JobRecord?> FindSubmissionAsync(string ownerId, string key, string hash, CancellationToken token)
    {
        lock (_gate)
        {
            if (!Idempotency.TryGetValue((ownerId, key), out var value)) return Task.FromResult<JobRecord?>(null);
            if (value.Hash != hash) throw new JobException("IDEMPOTENCY_CONFLICT", 409, "Key conflict.");
            return Task.FromResult<JobRecord?>(Jobs[value.Id]);
        }
    }
    public Task<bool> AcceptAsync(JobRecord job, string key, Guid uploadId, CancellationToken token)
    {
        lock (_gate)
        {
            if (FailAcceptance || Idempotency.ContainsKey((job.OwnerId, key))
                || !Uploads.TryGetValue(uploadId, out var upload) || upload.Object != job.Input || upload.OwnerId != job.OwnerId)
                return Task.FromResult(false);
            Jobs.Add(job.Id, job); Idempotency.Add((job.OwnerId, key), (job.Id, job.RequestHash)); Outbox.Add(job.DispatchId);
            return Task.FromResult(true);
        }
    }
    public Task<bool> CompareExchangeAsync(JobRecord next, long expectedVersion, CancellationToken token, ExecutionClaim? claim = null)
    {
        lock (_gate)
        {
            if (!Jobs.TryGetValue(next.Id, out var job) || job.Version != expectedVersion) return Task.FromResult(false);
            if (claim is not null && !JobRules.OwnsLease(job, claim, DateTimeOffset.UtcNow)) return Task.FromResult(false);
            Jobs[next.Id] = next; return Task.FromResult(true);
        }
    }
    public Task<IReadOnlyList<Guid>> DiscoverAsync(int limit, CancellationToken token)
    { lock (_gate) return Task.FromResult<IReadOnlyList<Guid>>(Jobs.Values.Where(j => !j.IsTerminal).Take(limit).Select(j => j.Id).ToArray()); }
    public Task<UploadRecord?> GetUploadAsync(Guid id, CancellationToken token) { lock (_gate) return Task.FromResult(Uploads.GetValueOrDefault(id)); }
    public Task<bool> PutUploadAsync(UploadRecord record, bool complete, CancellationToken token)
    {
        lock (_gate)
        {
            if (complete ? !Uploads.TryGetValue(record.Id, out var current) || current.Object is not null || current.OwnerId != record.OwnerId : Uploads.ContainsKey(record.Id))
                return Task.FromResult(false);
            Uploads[record.Id] = record; return Task.FromResult(true);
        }
    }
}
