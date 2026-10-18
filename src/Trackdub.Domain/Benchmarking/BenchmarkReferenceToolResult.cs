namespace Trackdub.Domain.Benchmarking;

public enum BenchmarkReferenceToolKind
{
    None,
    TensorRtRtx,
    OrtPerfTest
}

public enum BenchmarkReferenceStatus
{
    NotRequested,
    NotAvailable,
    UnsupportedVersion,
    UnsupportedWorkload,
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    MetricsUnavailable
}

/// <summary>A native-tool metric retains its own timing domain and unit.</summary>
public sealed record BenchmarkReferenceMetric(
    string Name,
    double Value,
    string Unit,
    string TimingDomain);

public sealed record BenchmarkReferenceToolResult
{
    public BenchmarkReferenceToolKind Tool { get; init; }
    public BenchmarkReferenceStatus Status { get; init; }
    public string GraphRole { get; init; } = "single";
    public bool Required { get; init; }
    public string? ExecutablePath { get; init; }
    public string? ExecutableIdentity { get; init; }
    public string? HelpIdentity { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public int? ExitCode { get; init; }
    public string? StandardOutputLogPath { get; init; }
    public string? StandardErrorLogPath { get; init; }
    public string? StandardOutputIdentity { get; init; }
    public string? StandardErrorIdentity { get; init; }
    public bool StandardOutputTruncated { get; init; }
    public bool StandardErrorTruncated { get; init; }
    public double? ElapsedMilliseconds { get; init; }
    public string? TimingDomain { get; init; }
    public IReadOnlyList<BenchmarkReferenceMetric> Metrics { get; init; } = [];
    public IReadOnlyList<string> Limitations { get; init; } = [];
    public string? FailureReason { get; init; }
}
