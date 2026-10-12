using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Application.Runtime;

/// <summary>
/// Installs vendor GPU runtimes used by starter packs and Model Manager (TRT RTX plugin, WinML catalog EPs).
/// </summary>
public interface IGpuRuntimeInstallOrchestrator
{
    Task<GpuRuntimeInstallResult> InstallAsync(
        StarterPackGpuRuntimeKind runtimeKind,
        IProgress<string> progress,
        CancellationToken cancellationToken = default,
        IProgress<double>? percentProgress = null);
}

public sealed record GpuRuntimeInstallResult(
    bool Succeeded,
    string? Detail = null,
    string? FailureDetail = null);
