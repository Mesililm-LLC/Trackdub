using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Trackdub.Contracts.Jobs;

namespace Trackdub.CloudJobAdapter;

public sealed record CliExecutionSpec(string Executable, IReadOnlyList<string> Arguments, string Workspace, string ResultPath);
public sealed record CliExitEvidence(int ExitCode, WorkerRunOutcome? Outcome);

public sealed partial class CliProcessRunner
{
    public async Task<CliExitEvidence> RunAsync(CliExecutionSpec spec, CancellationToken token)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsLinux() ? "/usr/bin/setsid" : spec.Executable)
        {
            WorkingDirectory = spec.Workspace,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (OperatingSystem.IsLinux()) start.ArgumentList.Add(spec.Executable);
        foreach (string arg in spec.Arguments) start.ArgumentList.Add(arg);
        string temporary = Path.Join(spec.Workspace, "tmp"); Directory.CreateDirectory(temporary);
        start.Environment["TMPDIR"] = temporary;
        start.Environment["TEMP"] = temporary;
        start.Environment["TMP"] = temporary;
        using var process = new Process { StartInfo = start };
        await using var stdout = File.Create(Path.Join(spec.Workspace, "cli.stdout.log"));
        await using var stderr = File.Create(Path.Join(spec.Workspace, "cli.stderr.log"));
        Task output = Task.CompletedTask;
        Task error = Task.CompletedTask;
        process.Start();
        try
        {
            process.StandardInput.Close();
            // Stream to bounded files instead of buffering unbounded child output in RAM.
            output = CopyBoundedAsync(process.StandardOutput.BaseStream, stdout);
            error = CopyBoundedAsync(process.StandardError.BaseStream, stderr);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                if (OperatingSystem.IsLinux()) _ = Kill(-process.Id, 15);
                using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (OperatingSystem.IsLinux()) _ = Kill(-process.Id, 9);
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            // A descendant that ignored SIGTERM can keep the inherited pipes open; reap the group before draining them.
            if (OperatingSystem.IsLinux()) _ = Kill(-process.Id, 9);
            await DrainAsync(output, error).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (OperatingSystem.IsLinux()) _ = Kill(-process.Id, 9);
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        await Task.WhenAll(output, error).ConfigureAwait(false);
        WorkerRunOutcome? outcome = null;
        if (process.ExitCode == 0 && File.Exists(spec.ResultPath))
        {
            if (new FileInfo(spec.ResultPath).Length > 2 * 1024 * 1024) return new(process.ExitCode, null);
            try
            {
                await using var stream = File.OpenRead(spec.ResultPath);
                outcome = await JsonSerializer.DeserializeAsync<WorkerRunOutcome>(stream, WorkerRunOutcome.JsonOptions, token).ConfigureAwait(false);
            }
            catch (JsonException) { /* Invalid structured evidence never becomes success. */ }
        }
        return new(process.ExitCode, outcome);
    }

    private static async Task DrainAsync(Task output, Task error)
    {
        try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        catch (TimeoutException) { /* Captured logs are diagnostic only; cancellation must not hang on them. */ }
    }

    private static async Task CopyBoundedAsync(Stream source, Stream destination)
    {
        byte[] buffer = new byte[8192]; long stored = 0; int count;
        while ((count = await source.ReadAsync(buffer).ConfigureAwait(false)) != 0)
        {
            int keep = (int)Math.Min(count, Math.Max(0, 4 * 1024 * 1024 - stored));
            if (keep > 0) { await destination.WriteAsync(buffer.AsMemory(0, keep)).ConfigureAwait(false); stored += keep; }
        }
    }

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static partial int Kill(int pid, int signal);
}
