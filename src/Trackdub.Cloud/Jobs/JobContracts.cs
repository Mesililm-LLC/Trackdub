using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts.Jobs;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Cloud.Jobs;

public enum JobState { Accepted, Queued, Running, CancelRequested, Succeeded, Failed, Cancelled, TimedOut }
public sealed record CreateJobRequest(int SchemaVersion, string ProjectName, JobInput Input,
    string SourceLanguage, string TargetLanguage, string Preset);
public sealed record JobInput(Guid UploadId);
public sealed record ObjectReference(string Key, string VersionId, long SizeBytes, string Sha256);
public sealed record ArtifactReference(string Id, string Kind, string FileName, string ContentType, ObjectReference Object);
public sealed record JobError(string Code, string Message, bool Retryable, string? StageKey, string CorrelationId);
public sealed record AttemptOutcome(ExecutionClaim Claim, ObjectReference ResultObject, int CliExitCode, IReadOnlyList<ArtifactReference> Artifacts);
public sealed record ExecutionClaim(Guid JobId, Guid AttemptId, Guid DispatchId, string ExecutionId, long Generation);
public sealed record JobProgress(Guid AttemptId, string? RunId, long Sequence, string? StageKey, string? Phase, double? PercentComplete, DateTimeOffset UpdatedAt);
public sealed record ArtifactDto(string Id, string Kind, string FileName, string ContentType, long SizeBytes, string Sha256,
    bool Available, string? DownloadUrl = null, DateTimeOffset? ExpiresAt = null);
public sealed record JobResult(int SchemaVersion, Guid RunId, string PipelineStatus, IReadOnlyList<string> Warnings, IReadOnlyList<ArtifactDto> Artifacts);
public sealed record JobDto(int SchemaVersion, Guid Id, JobState State, string? Phase, Guid? AttemptId, int AttemptNumber,
    long Version, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    DateTimeOffset? CancelRequestedAt, DateTimeOffset QueueDeadlineAt, DateTimeOffset? ExecutionDeadlineAt,
    JobProgress? Progress, JobResult? Result, JobError? Error)
{
    public static JobDto FromRecord(JobRecord job) => new(job.SchemaVersion, job.Id, job.State, job.Phase, job.AttemptId,
        job.AttemptNumber, job.Version, job.CreatedAt, job.UpdatedAt, job.StartedAt, job.CompletedAt, job.CancelRequestedAt,
        job.QueueDeadlineAt, job.ExecutionDeadlineAt, job.Progress, job.Result, job.Error);
}
public sealed record UploadRecord(Guid Id, string OwnerId, ObjectReference? Object, string Key, long SizeBytes,
    string Sha256, string ContentType, DateTimeOffset ExpiresAt);
public sealed record JobRecord
{
    public int SchemaVersion { get; init; } = 1;
    public required Guid Id { get; init; }
    public required string OwnerId { get; init; }
    public required CreateJobRequest Request { get; init; }
    public required ObjectReference Input { get; init; }
    public required string RequestHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
    public required DateTimeOffset QueueDeadlineAt { get; init; }
    public DateTimeOffset? ExecutionDeadlineAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public DateTimeOffset? CancelRequestedAt { get; init; }
    public JobState State { get; init; } = JobState.Accepted;
    public string? Phase { get; init; } = "dispatching";
    public long Version { get; init; }
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public int AttemptNumber { get; init; } = 1;
    public Guid DispatchId { get; init; } = Guid.NewGuid();
    public string? ExecutionId { get; init; }
    public long Generation { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }
    public DateTimeOffset? DispatchLeaseExpiresAt { get; init; }
    public JobProgress? Progress { get; init; }
    public AttemptOutcome? Candidate { get; init; }
    public IReadOnlyList<ArtifactReference> Artifacts { get; init; } = [];
    public JobError? Error { get; init; }
    public JobResult? Result { get; init; }
    [JsonIgnore] public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.TimedOut;
}

public static class CloudJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

public sealed class JobException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public interface IJobStore
{
    Task<JobRecord?> GetAsync(Guid id, CancellationToken token);
    Task<JobRecord?> FindSubmissionAsync(string ownerId, string key, string hash, CancellationToken token);
    Task<bool> AcceptAsync(JobRecord job, string idempotencyKey, Guid uploadId, CancellationToken token);
    Task<bool> CompareExchangeAsync(JobRecord next, long expectedVersion, CancellationToken token, ExecutionClaim? claim = null);
    Task<IReadOnlyList<Guid>> DiscoverAsync(int limit, CancellationToken token);
    Task<UploadRecord?> GetUploadAsync(Guid id, CancellationToken token);
    Task<bool> PutUploadAsync(UploadRecord record, bool complete, CancellationToken token);
}

public interface IArtifactStore
{
    Task VerifyAsync(ObjectReference reference, CancellationToken token);
    Task<string> SignDownloadAsync(ObjectReference reference, CancellationToken token);
    Task<string> SignUploadAsync(UploadRecord upload, CancellationToken token);
    Task<ObjectReference> CompleteUploadAsync(UploadRecord upload, string versionId, CancellationToken token);
    Task DownloadAsync(ObjectReference reference, string destination, CancellationToken token);
    Task<ObjectReference> UploadAsync(string key, string path, CancellationToken token);
    Task<WorkerRunOutcome> ReadOutcomeAsync(ObjectReference reference, CancellationToken token);
    Task<AttemptOutcome?> RecoverCandidateAsync(JobRecord job, CancellationToken token);
}

public sealed record SchedulerExecution(string Id, bool Stopped, int? ExitCode);
public sealed record DispatchRequest(Guid JobId, Guid AttemptId, Guid DispatchId);
public interface IJobScheduler
{
    Task<SchedulerExecution> SubmitAsync(DispatchRequest request, CancellationToken token);
    Task<IReadOnlyList<SchedulerExecution>> DescribeAsync(Guid dispatchId, CancellationToken token);
    Task StopAsync(string executionId, string reason, CancellationToken token);
}
