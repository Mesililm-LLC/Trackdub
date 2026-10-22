using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts.Dubbing;

namespace Trackdub.Contracts.Jobs;

/// <summary>Strict process result. Consumers must also verify process exit and artifact bytes.</summary>
public sealed record WorkerRunOutcome(
    [property: JsonRequired] int SchemaVersion,
    [property: JsonRequired] string Status,
    [property: JsonRequired] DubbingRunResult? Result,
    [property: JsonRequired] string? ErrorCode,
    [property: JsonRequired] string? Message,
    [property: JsonRequired] IReadOnlyList<WorkerArtifact> Artifacts)
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}

/// <summary>Verified local output, relative to the isolated project directory.</summary>
public sealed record WorkerArtifact([property: JsonRequired] string Path, [property: JsonRequired] string Kind,
    [property: JsonRequired] long SizeBytes, [property: JsonRequired] string Sha256);
