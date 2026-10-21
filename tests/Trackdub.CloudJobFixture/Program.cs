using System.Runtime.InteropServices;
using System.Text.Json;
using Trackdub.Contracts.Jobs;
using Trackdub.Contracts.Dubbing;
using System.Security.Cryptography;

if (args.Contains("--result-file"))
{
    string Value(string key) => args[Array.IndexOf(args, key) + 1];
    string project = Value("--output"); Directory.CreateDirectory(project);
    await File.AppendAllTextAsync(Path.Join(project, "fake-pipeline-invocations.txt"), "invoked\n");
    string video = Path.Join(project, "output.mp4"); File.Copy(Value("--media"), video);
    byte[] bytes = await File.ReadAllBytesAsync(video);
    var now = DateTimeOffset.UtcNow;
    var run = new DubbingRunResult
    {
        RunId = Guid.NewGuid(), StartTime = now, EndTime = now, OverallStatus = DubbingRunStatus.Succeeded,
        StageOutcomes = new[] { "asr", "translation", "tts", "export" }.Select(stage => new StageOutcome
        { StageName = stage, Status = StageStatus.Succeeded, StartTime = now, EndTime = now, ArtifactPaths = stage == "export" ? [video] : [] }).ToArray(),
    };
    await File.WriteAllTextAsync(Value("--result-file"), JsonSerializer.Serialize(new WorkerRunOutcome(1, "succeeded", run, null, null,
        [new WorkerArtifact("output.mp4", "video", bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)))]), WorkerRunOutcome.JsonOptions));
    return 0;
}

string mode = args[0];
string result = args[1];
if (mode == "echo") { await File.WriteAllTextAsync(result, JsonSerializer.Serialize(args[2..])); return 0; }
if (mode == "hang")
{
    using var cancel = new CancellationTokenSource();
    using var signal = OperatingSystem.IsLinux() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; cancel.Cancel(); }) : null;
    await File.WriteAllTextAsync(result + ".started", "started");
    try { await Task.Delay(Timeout.Infinite, cancel.Token); } catch (OperationCanceledException) { return 130; }
}
if (mode != "missing")
    await File.WriteAllTextAsync(result, JsonSerializer.Serialize(new WorkerRunOutcome(1, mode == "failed" ? "failed" : "succeeded", null, null, null, []), WorkerRunOutcome.JsonOptions));
Console.WriteLine("{\"status\":\"succeeded\"}");
return mode == "crash" ? 139 : 0;

public sealed class ProcessFixtureMarker;
