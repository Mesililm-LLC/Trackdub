using Trackdub.Domain;
using System.Text.Json.Serialization;

namespace Trackdub.Inference.Runtime.Planning;

public sealed record StageRuntimePlan
{
    public RuntimeStage Stage { get; init; }

    public StageRuntimePlanStatus Status { get; init; }

    public string? ModelId { get; init; }

    public string? ModelAlias { get; init; }

    public string? EngineFamily { get; init; }

    public string? ModelTier { get; init; }

    public string? Variant { get; init; }

    public ExecutionProviderKind? ExecutionProvider { get; init; }

    /// <summary>
    /// When <see langword="true"/>, the caller required the preferred EP (hard pin).
    /// Session factories must not soft-retry TensorRT RTX init onto DirectML/CPU.
    /// </summary>
    public bool RequirePreferredExecutionProvider { get; init; }

    public RuntimeModelIntegrityStatus ModelIntegrityStatus { get; init; }

    [JsonIgnore]
    public string? ModelEntryPath { get; init; }

    [JsonIgnore]
    public string? ModelRootPath { get; init; }

    [JsonIgnore]
    public string? ModelEntryRelativePath { get; init; }

    [JsonIgnore]
    public IReadOnlyList<string> RequiredModelRelativePaths { get; init; } = [];

    [JsonIgnore]
    public bool IsLocalOptimizedVariant { get; init; }

    /// <summary>
    /// Resolved model revision/content digest (manifest sha256) captured during planning;
    /// feeds residency identity so a same-path weights replacement cannot reuse stale
    /// state. This is an identity value, not an integrity verdict — it does not imply the
    /// payload was verified; <see cref="ModelIntegrityStatus"/> remains the source of
    /// truth (it may be <see cref="RuntimeModelIntegrityStatus.Skipped"/>). Null on
    /// non-runnable plans.
    /// </summary>
    [JsonIgnore]
    public string? ModelRevisionHash { get; init; }

    public int? DeviceIndex { get; init; }

    public string? DeviceAdapterDescription { get; init; }

    public RuntimePlanFallback? Fallback { get; init; }

    public IReadOnlyList<RuntimePlanWarning> Warnings { get; init; } = [];
}

public sealed record RuntimePlanFallback(
    RuntimePlanFallbackCode Code,
    string? Detail = null);

public sealed record RuntimePlanWarning(
    RuntimePlanWarningCode Code,
    string? Detail = null);

public sealed record HardwareProfile(
    string OperatingSystem,
    string Architecture,
    bool HasGpu,
    string? GpuDescription = null,
    IReadOnlyList<DeviceEntry>? Devices = null,
    string? CpuName = null,
    long TotalRamMb = 0,
    long DedicatedVramMb = 0,
    NvidiaGpuArchitectureBucket NvidiaGpuArchitecture = NvidiaGpuArchitectureBucket.Unknown,
    string? GpuDriverVersion = null,
    string? OrtRuntimeVersion = null);

/// <param name="EngineFamilies">
/// When set, only these engine families may run on the provider: it is reachable only through a
/// host that runs those engines (for example the ORT 1.30 inference worker, ADR-0017).
/// </param>
public sealed record ExecutionProviderAvailability(
    ExecutionProviderKind Provider,
    bool IsAvailable,
    string? Detail = null,
    IReadOnlyList<string>? EngineFamilies = null);

public sealed record ExecutionProviderSmokeTestRequest(
    RuntimeStage Stage,
    string ModelId,
    string ModelAlias,
    string? EngineFamily,
    string Variant,
    ExecutionProviderKind ExecutionProvider,
    string ModelRootPath,
    string EntryPath,
    string? ModelRevisionHash = null);

public sealed record ExecutionProviderSmokeTestResult(
    bool Passed,
    string? Detail = null);
