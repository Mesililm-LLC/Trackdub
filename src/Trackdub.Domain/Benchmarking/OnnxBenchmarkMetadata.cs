namespace Trackdub.Domain.Benchmarking;

public enum BenchmarkTimingMode
{
    ManagedOrtRunEndToEnd,
    DeviceBoundSynchronized
}

public enum BenchmarkSamplingPhase
{
    FirstRun,
    Warmup,
    Measurement
}

public enum BenchmarkCachePhase
{
    BeforeSession,
    SessionInitialized,
    FirstRun,
    Warmup,
    Measurement
}

public enum BenchmarkHistoryPersistenceStatus
{
    NotRequested,
    Saved,
    Failed
}

public sealed record BenchmarkHistoryPersistenceResult(
    BenchmarkHistoryPersistenceStatus Status,
    string? FailureReason = null);

/// <summary>Versioned, typed evidence accompanying one ONNX workload measurement.</summary>
public sealed record BenchmarkRunMetadata
{
    public int SchemaVersion { get; init; } = 1;
    public BenchmarkEnvironmentInfo? Environment { get; init; }
    public BenchmarkConfigurationInfo? Configuration { get; init; }
    public BenchmarkWorkDefinition? WorkDefinition { get; init; }
    public IReadOnlyList<BenchmarkGraphMetadata> Graphs { get; init; } = [];
    public BenchmarkSamplingInfo? Sampling { get; init; }
    public string? ComparisonKey { get; init; }
    public Guid? ComparisonGroupId { get; init; }
    public IReadOnlyList<BenchmarkReferenceToolResult> ReferenceToolResults { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record BenchmarkEnvironmentInfo
{
    public string? CpuName { get; init; }
    public long? PhysicalRamBytes { get; init; }
    public long? GcAvailableMemoryBytes { get; init; }
    public string? OsDescription { get; init; }
    public string? ProcessArchitecture { get; init; }
    public string? DotnetVersion { get; init; }
    public string? OrtManagedAssemblyVersion { get; init; }
    public string? HostOrtRuntimeVersion { get; init; }
    public string? MachineGpuName { get; init; }
    public string? GpuArchitecture { get; init; }
    public string? DriverVersion { get; init; }
    public string? SelectedDeviceIdentity { get; init; }
    public string? ConfiguredPluginFingerprint { get; init; }
    public string? ObservedPluginIdentity { get; init; }
    public string? ExpectedPluginVersion { get; init; }
    public string? ExpectedTensorRtRtxVersion { get; init; }
    public string? EpContextEnvironmentFingerprint { get; init; }
    public DateTimeOffset CapturedAtUtc { get; init; }
    public IReadOnlyList<string> UnavailableReasons { get; init; } = [];
}

public sealed record BenchmarkConfigurationInfo
{
    public string? RequestedProvider { get; init; }
    public string? SelectedProvider { get; init; }
    public string? WindowsMlDevicePolicyKey { get; init; }
    public int RequestedRunCount { get; init; }
    public BenchmarkExecutionSettings Execution { get; init; } = BenchmarkExecutionSettings.Default;
    public BenchmarkTimingMode TimingMode { get; init; } = BenchmarkTimingMode.ManagedOrtRunEndToEnd;
    public string? InputProfileId { get; init; }
    public string? ModelProfileId { get; init; }
    public bool ReferenceToolRequested { get; init; }
    public bool HistoryRequested { get; init; }
}

public sealed record BenchmarkWorkDefinition(
    string Scenario,
    string Description,
    BenchmarkThroughputUnit ThroughputUnit,
    double? AudioSecondsPerPass = null,
    double? ItemsPerPass = null);

public sealed record BenchmarkTensorDescription(
    string Name,
    string DataType,
    IReadOnlyList<int> Dimensions,
    IReadOnlyList<string>? SymbolicDimensions = null);

public sealed record BenchmarkGraphMetadata
{
    public string Role { get; init; } = "single";
    public string? SourceModelPath { get; init; }
    public string? SourceModelSha256 { get; init; }
    public bool ExternalDataIdentityVerified { get; init; }
    public string? LoadedModelPath { get; init; }
    public string? LoadKind { get; init; }
    public string? ArtifactIdentity { get; init; }
    public string? CompileOptionsIdentity { get; init; }
    public string? RuntimeCacheDirectoryIdentity { get; init; }
    public bool EnableCudaGraph { get; init; }
    public int? SelectedDeviceOrdinal { get; init; }
    public string? SelectedDeviceIdentity { get; init; }
    public IReadOnlyDictionary<string, string> EffectiveProviderOptions { get; init; } =
        new Dictionary<string, string>();
    public IReadOnlyList<BenchmarkTensorDescription> Inputs { get; init; } = [];
    public IReadOnlyList<BenchmarkTensorDescription> Outputs { get; init; } = [];
    public IReadOnlyList<BenchmarkCacheObservation> CacheObservations { get; init; } = [];
}

public sealed record BenchmarkCacheObservation(
    BenchmarkCachePhase Phase,
    bool? DirectoryExists,
    int? FileCount,
    long? TotalBytes,
    string? Outcome = null,
    string? UnavailableReason = null);

public sealed record BenchmarkSamplingInfo(
    bool PolicyCompleted,
    int WarmupRunCount,
    double WarmupMilliseconds,
    int SampleCount,
    double MeasuredWallTimeMilliseconds,
    string Qualification = "WarmupPolicyOnly");
