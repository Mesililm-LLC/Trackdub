using Trackdub.Application.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// What to make resident before the first pipeline run. <see cref="Selections"/> are the same
/// runtime model selections the run will use, so the warmed model/provider matches it.
/// </summary>
public sealed record ResidentWarmupRequest(
    IReadOnlyList<RuntimeStage> Stages,
    RuntimeModelSelections Selections,
    string? SourceLanguageCode = null,
    string? TargetLanguageCode = null,
    bool RequiresVoiceClone = false,
    bool BootstrapProviders = true)
{
    /// <summary>Early pipeline stages first, so the stages that run first warm first.</summary>
    public static IReadOnlyList<RuntimeStage> DefaultStages { get; } =
    [
        RuntimeStage.Vad,
        RuntimeStage.Asr,
        RuntimeStage.Separation,
        RuntimeStage.Translation,
        RuntimeStage.Tts,
    ];
}

public sealed record ResidentProviderBootstrapOutcome(ExecutionProviderKind Provider, bool Ready, string? Detail);

public sealed record ResidentStageWarmupOutcome(
    RuntimeStage Stage,
    bool Attempted,
    bool Succeeded,
    TimeSpan Duration,
    string? Detail);

public sealed record ResidentWarmupReport(
    bool Skipped,
    string? SkipReason,
    IReadOnlyList<ResidentProviderBootstrapOutcome> Providers,
    IReadOnlyList<ResidentStageWarmupOutcome> Stages);

/// <summary>
/// Pays first-use cost ahead of the first pipeline run: registers every available execution
/// provider (Windows ML catalog EnsureReady/TryRegister, never downloading) and warms each
/// stage's planner-selected model into the session pool. Optimization only: failures are
/// reported per provider/stage and never thrown; cancellation is.
/// </summary>
public interface IResidentWarmupService
{
    /// <summary>Set to 0 or false to turn resident warmup off for the process.</summary>
    const string DisableEnvironmentVariable = "TRACKDUB_RESIDENT_WARMUP";

    Task<ResidentWarmupReport> WarmAsync(
        ResidentWarmupRequest request,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
