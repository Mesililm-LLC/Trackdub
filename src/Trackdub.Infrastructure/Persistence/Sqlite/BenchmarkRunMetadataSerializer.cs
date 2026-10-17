using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

internal static class BenchmarkRunMetadataSerializer
{
    internal const int SupportedSchemaVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string? Serialize(BenchmarkRunMetadata? metadata) =>
        metadata is null ? null : JsonSerializer.Serialize(metadata, JsonOptions);

    public static BenchmarkRunMetadataReadResult Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BenchmarkRunMetadataReadResult(null, null);
        }

        try
        {
            BenchmarkRunMetadata? metadata = JsonSerializer.Deserialize<BenchmarkRunMetadata>(json, JsonOptions);
            return metadata is { SchemaVersion: > SupportedSchemaVersion }
                ? new BenchmarkRunMetadataReadResult(
                    null, $"benchmark-metadata-version-unsupported: schemaVersion {metadata.SchemaVersion} exceeds supported {SupportedSchemaVersion}")
                : new BenchmarkRunMetadataReadResult(metadata, null);
        }
        catch (JsonException exception)
        {
            return new BenchmarkRunMetadataReadResult(
                null, $"benchmark-metadata-unreadable: {exception.Message}");
        }
    }

    public static BenchmarkRunMetadata? ToRecordMetadata(BenchmarkRunMetadataReadResult read) =>
        read.Metadata ??
        (read.Diagnostic is null
            ? null
            : new BenchmarkRunMetadata { Warnings = [read.Diagnostic] });
}

internal sealed record BenchmarkRunMetadataReadResult(BenchmarkRunMetadata? Metadata, string? Diagnostic);
