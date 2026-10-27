using Trackdub.Application.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Optimization-only stage warmup request. Carries the same runtime model selections the
/// stage will execute with so the warmed model/provider/profile matches the run.
/// </summary>
public sealed record StageWarmupRequest(
    RuntimeStage Stage,
    RuntimeModelSelections Selections,
    string? SourceLanguageCode = null,
    string? TargetLanguageCode = null,
    bool RequiresVoiceClone = false);

/// <summary>
/// Optimization result only. <see cref="Succeeded"/> does not imply stage readiness —
/// actual stage planning/execution remains the authority.
/// </summary>
public sealed record StageWarmupResult(
    bool Attempted,
    bool Succeeded,
    string? Detail = null);

/// <summary>
/// Warms the selected model/provider for an imminent runtime stage while its preceding
/// stage executes. Implementations must not create stage-run records, artifacts, readiness
/// success, or progress events. A failed result is not optimization-only: the engine
/// consumes it as the target stage's skip (reason <c>WARMUP_FAILED</c>) before inference,
/// and implementations must record no readiness verdict for a failed smoke.
/// </summary>
public interface IStageWarmupCoordinator
{
    Task<StageWarmupResult> WarmAsync(
        StageWarmupRequest request,
        CancellationToken cancellationToken = default);
}
