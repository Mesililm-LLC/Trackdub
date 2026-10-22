using System.Runtime.InteropServices;
using Amazon.DynamoDBv2;
using Amazon.S3;
using Trackdub.Cloud.Aws;
using Trackdub.Cloud.Jobs;
using Trackdub.CloudJobAdapter;
using System.Text.Json;
using Trackdub.Contracts.Jobs;

if (args.Length > 0 && args[0] == "verify-artifacts")
{
    if (args.Length != 5 || args[3] != "0" || args[4] != "0") return 2;
    try
    {
        if (new FileInfo(args[1]).Length > 2 * 1024 * 1024) return 2;
        var outcome = JsonSerializer.Deserialize<WorkerRunOutcome>(await File.ReadAllTextAsync(args[1]), WorkerRunOutcome.JsonOptions);
        if (outcome is null || !JobController.PipelineGoalAchieved(outcome)) return 2;
        foreach (var artifact in outcome.Artifacts)
            await JobAdapter.VerifyArtifactAsync(args[2], artifact, CancellationToken.None);
        Console.WriteLine("Local result and artifact checks passed. Durable AWS job status requires separate verification.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return 2; }
}

string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
    ? value : throw new InvalidOperationException($"Required configuration missing: {name}");
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
using var termination = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{ context.Cancel = true; shutdown.Cancel(); });
using var dynamo = new AmazonDynamoDBClient();
using var s3 = new AmazonS3Client();
var store = new DynamoDbJobStore(dynamo, Required("TRACKDUB_CLOUD_TABLE"), TimeProvider.System);
var objects = new S3ArtifactStore(s3, Required("TRACKDUB_CLOUD_BUCKET"));
var jobs = new JobService(store, objects, TimeProvider.System);
var identity = new ExecutionClaim(Guid.Parse(Required("TRACKDUB_JOB_ID")), Guid.Parse(Required("TRACKDUB_ATTEMPT_ID")),
    Guid.Parse(Required("TRACKDUB_DISPATCH_ID")), Required("TRACKDUB_EXECUTION_ID"), 0);
try
{
    return await new JobAdapter(store, jobs, objects, new CliProcessRunner(), "/app/Trackdub.Cli.dll", "/data/jobs").RunAsync(identity, shutdown.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Supervisor failed ({ex.GetType().Name}); scheduler reconciliation determines terminal state.");
    return 2;
}
