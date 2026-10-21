using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Cloud.Jobs;
using Trackdub.CloudJobAdapter;
using Trackdub.Contracts.Jobs;
using Trackdub.TestDoubles;

namespace Trackdub.CloudJobAdapter.Tests;

public sealed class JobAdapterTests
{
    [Fact]
    public async Task PublicationRetriesNeverInvokeFakePipelineTwice()
    {
        string root = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var store = new FakeJobStore(); var objects = new LocalObjects(); var jobs = new JobService(store, objects, TimeProvider.System);
        var now = DateTimeOffset.UtcNow; var upload = Guid.NewGuid();
        var input = new ObjectReference("input.mp4", "input-version", objects.Input.Length, Convert.ToHexStringLower(SHA256.HashData(objects.Input)));
        var job = new JobRecord { Id = Guid.NewGuid(), OwnerId = "owner", RequestHash = "hash", Input = input,
            Request = new(1, "Fixture", new(upload), "en", "es", "poc-server-v1"), CreatedAt = now, UpdatedAt = now, QueueDeadlineAt = now.AddHours(1) };
        store.Jobs[job.Id] = job;
        try
        {
            int exit = await new JobAdapter(store, jobs, objects, new(), typeof(ProcessFixtureMarker).Assembly.Location, root)
                .RunAsync(new(job.Id, job.AttemptId, job.DispatchId, "execution", 0), TestContext.Current.CancellationToken);
            Assert.Equal(0, exit);
            string project = Path.Join(root, job.Id.ToString(), job.AttemptId.ToString(), "1", "project");
            Assert.Single(await File.ReadAllLinesAsync(Path.Join(project, "fake-pipeline-invocations.txt"), TestContext.Current.CancellationToken));
            Assert.Equal(1, objects.InjectedFailures);
            Assert.NotNull(store.Jobs[job.Id].Candidate);
            Assert.Contains(store.Jobs[job.Id].Candidate!.Artifacts, a => a.Kind == "runResult");
            Assert.Equal(JobState.Running, store.Jobs[job.Id].State); // Scheduler cessation and final CAS are still required.
            string attempt = Path.GetDirectoryName(project)!;
            string checkRoot = Path.Join(root, "artifact-check"); Directory.CreateDirectory(checkRoot);
            foreach (string cliExit in new[] { "0", "139" })
            {
                var check = await new CliProcessRunner().RunAsync(new("dotnet",
                    [typeof(JobAdapter).Assembly.Location, "verify-artifacts", Path.Join(attempt, "result.json"), project, cliExit, "0"],
                    checkRoot, Path.Join(checkRoot, "unused-result.json")), TestContext.Current.CancellationToken);
                Assert.Equal(cliExit == "0" ? 0 : 2, check.ExitCode);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class LocalObjects : IArtifactStore
    {
        public byte[] Input { get; } = File.ReadAllBytes(Path.Join(AppContext.BaseDirectory, "Fixtures", "en-speech.mp4"));
        public int InjectedFailures { get; private set; }
        private readonly Dictionary<ObjectReference, byte[]> _objects = [];
        public async Task DownloadAsync(ObjectReference reference, string destination, CancellationToken token) => await File.WriteAllBytesAsync(destination, Input, token);
        public async Task<ObjectReference> UploadAsync(string key, string path, CancellationToken token)
        {
            if (InjectedFailures == 0) { InjectedFailures++; throw new IOException("Injected interrupted transfer."); }
            byte[] bytes = await File.ReadAllBytesAsync(path, token);
            var reference = new ObjectReference(key, Guid.NewGuid().ToString(), bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            _objects[reference] = bytes; return reference;
        }
        public Task VerifyAsync(ObjectReference reference, CancellationToken token) { Assert.True(_objects.ContainsKey(reference)); return Task.CompletedTask; }
        public Task<string> SignDownloadAsync(ObjectReference reference, CancellationToken token) => throw new NotSupportedException();
        public Task<string> SignUploadAsync(UploadRecord upload, CancellationToken token) => throw new NotSupportedException();
        public Task<ObjectReference> CompleteUploadAsync(UploadRecord upload, string versionId, CancellationToken token) => throw new NotSupportedException();
        public Task<WorkerRunOutcome> ReadOutcomeAsync(ObjectReference reference, CancellationToken token) => Task.FromResult(JsonSerializer.Deserialize<WorkerRunOutcome>(_objects[reference], WorkerRunOutcome.JsonOptions)!);
        public Task<AttemptOutcome?> RecoverCandidateAsync(JobRecord job, CancellationToken token) => throw new NotSupportedException();
    }
}
