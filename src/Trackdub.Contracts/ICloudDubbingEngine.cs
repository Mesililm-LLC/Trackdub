using Trackdub.Contracts.Pipeline;

namespace Trackdub.Contracts;

public enum CloudDubbingStage
{
    Submitting = 0,
    Polling = 1,
    Downloading = 2,
    DeletingRemoteProject = 3,
    Completed = 4,
}

public sealed record CloudDubbingProgress(
    CloudDubbingStage Stage,
    string Message,
    string? DubbingId = null);

public interface ICloudDubbingEngine
{
    Task<CloudDubbingResult> DubAsync(CloudDubbingRequest request, CancellationToken cancellationToken);

    Task<StageReadiness> EvaluateReadinessAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new StageReadiness(
            StageName: "cloud-dub",
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: null,
            ResolveAction: null));

    Task DeleteDubbingProjectAsync(string dubbingId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed record CloudDubbingRequest(
    string MediaFilePath,
    string SourceLanguage,
    string TargetLanguage,
    IProgress<CloudDubbingProgress>? Progress = null,
    bool DeleteRemoteProjectOnCompletion = true);

public sealed record CloudDubbingResult(
    byte[] AudioBytes,
    string TargetLanguage,
    TimeSpan EstimatedDuration,
    string? DubbingId = null,
    bool RemoteProjectDeleted = false);
