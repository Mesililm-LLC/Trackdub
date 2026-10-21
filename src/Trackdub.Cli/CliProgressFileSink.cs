using System.Text.Json;
using Trackdub.Contracts.Jobs;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Cli;

/// <summary>Serializes dedicated progress independently of diagnostic output.</summary>
internal sealed class CliProgressFileSink : IProgress<PipelineProgressEvent>, IDisposable
{
    private readonly object _gate = new();
    private readonly StreamWriter _writer;
    private readonly IProgress<PipelineProgressEvent>? _next;

    internal CliProgressFileSink(string path, IProgress<PipelineProgressEvent>? next)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _writer = new StreamWriter(new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        _next = next;
    }

    public void Report(PipelineProgressEvent value)
    {
        lock (_gate)
        {
            _writer.WriteLine(JsonSerializer.Serialize(value, WorkerRunOutcome.JsonOptions));
            _writer.Flush();
            _next?.Report(value);
        }
    }

    public void Dispose() { lock (_gate) _writer.Dispose(); }
}
