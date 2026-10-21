using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Cloud.Jobs;
using Trackdub.Contracts.Jobs;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.CloudJobAdapter;

public sealed class JobAdapter(IJobStore store, JobService jobs, IArtifactStore objects, CliProcessRunner runner,
    string cliAssembly, string workspaceRoot)
{
    public async Task<int> RunAsync(ExecutionClaim identity, CancellationToken shutdown)
    {
        Directory.CreateDirectory(workspaceRoot);
        // Every job container on the host must bind the same local /data/jobs directory.
        using var hostLock = new FileStream(Path.Join(workspaceRoot, ".host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var claim = await jobs.ClaimAsync(identity, shutdown).ConfigureAwait(false);
        if (claim is null) return 2;
        var job = await store.GetAsync(claim.JobId, shutdown).ConfigureAwait(false) ?? throw new InvalidOperationException("Claimed job missing.");
        string workspace = Path.Join(Path.GetFullPath(workspaceRoot), job.Id.ToString(), job.AttemptId.ToString(), claim.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (Directory.Exists(workspace)) throw new IOException("Attempt workspace already exists.");
        Directory.CreateDirectory(workspace);
        string input = Path.Join(workspace, "input.mp4");
        string project = Path.Join(workspace, "project");
        string result = Path.Join(workspace, "result.json");
        string progress = Path.Join(workspace, "progress.jsonl");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        var lease = new ExecutionLeaseWatchdog(TimeProvider.System);
        Task watchdog = lease.RunAsync(deadline);
        Task heartbeat = HeartbeatAsync(claim, progress, deadline, lease);
        try
        {
            await objects.DownloadAsync(job.Input, input, deadline.Token).ConfigureAwait(false);
            await MediaValidator.ValidateAsync(input, deadline.Token).ConfigureAwait(false);
            // Fixed approved preset. Never accept client commands, paths, model IDs or environment overrides.
            string[] args = [cliAssembly, "--model-directory", "/data/models",
                "run", "pipeline", "--media", input, "--output", project, "--source-language", "en", "--target-language", "es",
                "--preset", "poc-server-v1", "--voice", "SPEAKER_00:ef_dora", "--subtitle-format", "srt",
                "--result-file", result, "--progress-file", progress];
            var exit = await runner.RunAsync(new("dotnet", args, workspace, result), deadline.Token).ConfigureAwait(false);
            if (exit.ExitCode != 0 || exit.Outcome is null || exit.Outcome.Status != "succeeded") return 2;
            var inventory = new List<ArtifactReference>();
            foreach (var artifact in exit.Outcome.Artifacts)
            {
                if (artifact.Kind is not ("video" or "subtitle")) throw new IOException("Artifact kind is not allowed.");
                string path = await VerifyArtifactAsync(project, artifact, deadline.Token).ConfigureAwait(false);
                if (artifact.Kind == "video") await MediaValidator.ValidateAsync(path, deadline.Token).ConfigureAwait(false);
                string key = JobService.AttemptPrefix(job) + "artifacts/" + inventory.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var published = await RetryAsync(() => objects.UploadAsync(key, path, deadline.Token), deadline.Token).ConfigureAwait(false);
                inventory.Add(new(artifact.Path, artifact.Kind, Path.GetFileName(path), artifact.Kind == "video" ? "video/mp4" : "text/plain", published));
            }
            var resultObject = await RetryAsync(() => objects.UploadAsync(JobService.AttemptPrefix(job) + "result.json", result, deadline.Token), deadline.Token).ConfigureAwait(false);
            inventory.Add(new("runResult", "runResult", "result.json", "application/json", resultObject));
            var candidate = new AttemptOutcome(claim, resultObject, exit.ExitCode, inventory);
            if (!JobController.OutcomeIsSuccessful(exit.Outcome, candidate)) return 2;
            // Keep the candidate itself durable before control-plane publication. Retrying publication never reruns CLI.
            string candidateFile = Path.Join(workspace, "candidate.json");
            await File.WriteAllTextAsync(candidateFile, JsonSerializer.Serialize(candidate, CloudJson.Options), deadline.Token).ConfigureAwait(false);
            await RetryAsync(() => objects.UploadAsync(JobService.AttemptPrefix(job) + "candidate.json", candidateFile, deadline.Token), deadline.Token).ConfigureAwait(false);
            try { await RetryAsync(() => jobs.ReportCandidateAsync(claim, candidate, deadline.Token), deadline.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is Amazon.Runtime.AmazonServiceException aws && (int)aws.StatusCode >= 500
                || ex is JobException jobError && jobError.StatusCode == 503)
            { /* Durable candidate allows reconciliation without repeating the pipeline. */ }
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        finally
        {
            deadline.Cancel();
            try { await heartbeat.ConfigureAwait(false); } catch (OperationCanceledException) { }
            await watchdog.ConfigureAwait(false);
        }
    }

    private async Task HeartbeatAsync(ExecutionClaim claim, string progressPath, CancellationTokenSource lifetime, ExecutionLeaseWatchdog lease)
    {
        DateTimeOffset lastRenewal = DateTimeOffset.UtcNow;
        long sequence = -1; long offset = 0; string pending = "";
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token).ConfigureAwait(false);
                try
                {
                    using var call = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    call.CancelAfter(TimeSpan.FromSeconds(10));
                    if (DateTimeOffset.UtcNow - lastRenewal >= TimeSpan.FromSeconds(15))
                    {
                        var job = await jobs.HeartbeatAsync(claim, call.Token).WaitAsync(call.Token).ConfigureAwait(false);
                        lastRenewal = DateTimeOffset.UtcNow;
                        lease.Renewed();
                        if (job.CancelRequestedAt is not null) { lifetime.Cancel(); return; }
                    }
                    if (!File.Exists(progressPath)) continue;
                    using var stream = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    stream.Position = offset;
                    using var reader = new StreamReader(stream);
                    string text = await reader.ReadToEndAsync(lifetime.Token).ConfigureAwait(false);
                    offset = stream.Position;
                    if (text.Length + pending.Length > 1024 * 1024) throw new IOException("Progress exceeded the bounded snapshot size.");
                    string[] lines = (pending + text).Split('\n'); pending = lines[^1];
                    PipelineProgressEvent? latest = null;
                    foreach (string line in lines[..^1])
                    {
                        var item = JsonSerializer.Deserialize<PipelineProgressEvent>(line, WorkerRunOutcome.JsonOptions);
                        if (item is not null && item.SequenceNumber > sequence) { sequence = item.SequenceNumber; latest = item; }
                    }
                    if (latest is not null) await jobs.ReportProgressAsync(claim, latest, call.Token).WaitAsync(call.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { /* Independent watchdog bounds stalled requests. */ }
                catch (JobException) { lifetime.Cancel(); return; }
                catch (Exception ex) when (ex is IOException or Amazon.Runtime.AmazonServiceException)
                {
                    // Stop before the 90s lease expires. Publication and process cancellation share this token.
                    if (DateTimeOffset.UtcNow - lastRenewal >= TimeSpan.FromSeconds(60)) { lifetime.Cancel(); return; }
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch { lifetime.Cancel(); throw; }
    }

    public static async Task<string> VerifyArtifactAsync(string project, WorkerArtifact artifact, CancellationToken token)
    {
        string root = Path.GetFullPath(project);
        if (Path.IsPathRooted(artifact.Path)) throw new IOException("Artifact path must be relative.");
        string path = Path.GetFullPath(Path.Join(root, artifact.Path));
        string relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new IOException("Artifact path escaped project.");
        for (string? part = path; part is not null; part = Path.GetDirectoryName(part))
        {
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symbolic artifact path rejected.");
            if (string.Equals(part, root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) break;
        }
        await using var stream = File.OpenRead(path);
        if (stream.Length <= 0 || stream.Length != artifact.SizeBytes
            || Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)) != artifact.Sha256)
            throw new IOException("Artifact checksum or size mismatch.");
        return path;
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (int retry = 0; ; retry++)
        {
            try { return await action().ConfigureAwait(false); }
            catch (Exception ex) when (retry < 4 && (ex is IOException || ex is Amazon.Runtime.AmazonServiceException aws && (int)aws.StatusCode >= 500
                || ex is JobException jobError && jobError.StatusCode == 503))
            { await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 1 << retry)), token).ConfigureAwait(false); }
        }
    }
}
