using Trackdub.Cloud.Jobs;
using Trackdub.Contracts.Jobs;

namespace Trackdub.TestDoubles;

public sealed class FakeCloudArtifactStore : IArtifactStore
{
    public WorkerRunOutcome? Outcome { get; set; }
    public bool FailVerification { get; set; }
    public AttemptOutcome? Candidate { get; set; }
    public Func<Task>? OnVerify { get; set; }
    public async Task VerifyAsync(ObjectReference reference, CancellationToken token)
    {
        if (FailVerification) throw new IOException("Incomplete upload.");
        if (OnVerify is not null) await OnVerify();
    }
    public Task<string> SignDownloadAsync(ObjectReference reference, CancellationToken token) => Task.FromResult("https://private.example/download");
    public Task<string> SignUploadAsync(UploadRecord upload, CancellationToken token) => Task.FromResult("https://private.example/upload");
    public async Task<ObjectReference> CompleteUploadAsync(UploadRecord upload, string versionId, CancellationToken token)
    { var r = new ObjectReference(upload.Key, versionId, upload.SizeBytes, upload.Sha256); await VerifyAsync(r, token); return r; }
    public Task DownloadAsync(ObjectReference reference, string destination, CancellationToken token) => throw new NotSupportedException();
    public Task<ObjectReference> UploadAsync(string key, string path, CancellationToken token) => throw new NotSupportedException();
    public Task<WorkerRunOutcome> ReadOutcomeAsync(ObjectReference reference, CancellationToken token) => Task.FromResult(Outcome!);
    public Task<AttemptOutcome?> RecoverCandidateAsync(JobRecord job, CancellationToken token) => Task.FromResult(Candidate);
}
