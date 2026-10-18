using System.Data.Common;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class BenchmarkRepository : IBenchmarkRepository
{
    public async Task AddAsync(
        DbConnection connection,
        BenchmarkRunRecord run,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO BenchmarkRuns (
                Id,
                ModelId,
                ModelPath,
                ReportPath,
                Status,
                RequestedProvider,
                SelectedProvider,
                RunCount,
                SupportsExecution,
                ModelSizeBytes,
                ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds,
                FailureReason,
                GeneratedAtUtc,
                FirstRunMilliseconds,
                WarmupMilliseconds,
                WarmupRunCount,
                SampleCount,
                WarmLatencyP50Milliseconds,
                WarmLatencyP90Milliseconds,
                WarmLatencyP95Milliseconds,
                WarmLatencyP99Milliseconds,
                MeasuredWallTimeMilliseconds,
                PassesPerSecond,
                ThroughputUnitsPerSecond,
                AudioDurationSeconds,
                RealTimeFactorAverage,
                ThroughputUnit,
                TimingMode,
                InputProfileId,
                ComparisonKey,
                MetadataJson)
            VALUES (
                @Id,
                @ModelId,
                @ModelPath,
                @ReportPath,
                @Status,
                @RequestedProvider,
                @SelectedProvider,
                @RunCount,
                @SupportsExecution,
                @ModelSizeBytes,
                @ColdLoadMilliseconds,
                @WarmLatencyAverageMilliseconds,
                @WarmLatencyMinimumMilliseconds,
                @WarmLatencyMaximumMilliseconds,
                @FailureReason,
                @GeneratedAtUtc,
                @FirstRunMilliseconds,
                @WarmupMilliseconds,
                @WarmupRunCount,
                @SampleCount,
                @WarmLatencyP50Milliseconds,
                @WarmLatencyP90Milliseconds,
                @WarmLatencyP95Milliseconds,
                @WarmLatencyP99Milliseconds,
                @MeasuredWallTimeMilliseconds,
                @PassesPerSecond,
                @ThroughputUnitsPerSecond,
                @AudioDurationSeconds,
                @RealTimeFactorAverage,
                @ThroughputUnit,
                @TimingMode,
                @InputProfileId,
                @ComparisonKey,
                @MetadataJson);
            """,
            new
            {
                Id = SqliteValueConverters.ToDbValue(run.Id),
                run.ModelId,
                run.ModelPath,
                run.ReportPath,
                Status = run.Status.ToString(),
                run.RequestedProvider,
                run.SelectedProvider,
                run.RunCount,
                SupportsExecution = run.SupportsExecution ? 1 : 0,
                run.ModelSizeBytes,
                run.ColdLoadMilliseconds,
                run.WarmLatencyAverageMilliseconds,
                run.WarmLatencyMinimumMilliseconds,
                run.WarmLatencyMaximumMilliseconds,
                run.FailureReason,
                GeneratedAtUtc = SqliteValueConverters.ToDbValue(run.GeneratedAtUtc),
                run.FirstRunMilliseconds,
                run.WarmupMilliseconds,
                run.WarmupRunCount,
                run.SampleCount,
                run.WarmLatencyP50Milliseconds,
                run.WarmLatencyP90Milliseconds,
                run.WarmLatencyP95Milliseconds,
                run.WarmLatencyP99Milliseconds,
                run.MeasuredWallTimeMilliseconds,
                run.PassesPerSecond,
                run.ThroughputUnitsPerSecond,
                run.AudioDurationSeconds,
                run.RealTimeFactorAverage,
                ThroughputUnit = run.ThroughputUnit?.ToString(),
                TimingMode = run.TimingMode?.ToString(),
                run.InputProfileId,
                run.ComparisonKey,
                MetadataJson = BenchmarkRunMetadataSerializer.Serialize(run.Metadata)
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListByModelIdAsync(
        DbConnection connection,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<BenchmarkRunRow> rows = (await connection.QueryAsync<BenchmarkRunRow>(new CommandDefinition(
            """
            SELECT
                Id,
                ModelId,
                ModelPath,
                ReportPath,
                Status,
                RequestedProvider,
                SelectedProvider,
                RunCount,
                SupportsExecution,
                ModelSizeBytes,
                ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds,
                FailureReason,
                GeneratedAtUtc,
                FirstRunMilliseconds,
                WarmupMilliseconds,
                WarmupRunCount,
                SampleCount,
                WarmLatencyP50Milliseconds,
                WarmLatencyP90Milliseconds,
                WarmLatencyP95Milliseconds,
                WarmLatencyP99Milliseconds,
                MeasuredWallTimeMilliseconds,
                PassesPerSecond,
                ThroughputUnitsPerSecond,
                AudioDurationSeconds,
                RealTimeFactorAverage,
                ThroughputUnit,
                TimingMode,
                InputProfileId,
                ComparisonKey,
                MetadataJson
            FROM BenchmarkRuns
            WHERE ModelId = @ModelId
            ORDER BY GeneratedAtUtc, Id;
            """,
            new { ModelId = modelId },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows
            .Select(row => new BenchmarkRunRecord(
                SqliteValueConverters.ParseGuid(row.Id),
                row.ModelId,
                row.ModelPath,
                row.ReportPath,
                Enum.TryParse(row.Status, ignoreCase: false, out BenchmarkStatus status) && Enum.IsDefined(status)
                    ? status
                    : BenchmarkStatus.Failed,
                row.RequestedProvider,
                row.SelectedProvider,
                (int)row.RunCount,
                row.SupportsExecution != 0,
                row.ModelSizeBytes,
                row.ColdLoadMilliseconds,
                row.WarmLatencyAverageMilliseconds,
                row.WarmLatencyMinimumMilliseconds,
                row.WarmLatencyMaximumMilliseconds,
                row.FailureReason,
                SqliteValueConverters.ParseDateTimeOffset(row.GeneratedAtUtc))
            {
                FirstRunMilliseconds = row.FirstRunMilliseconds,
                WarmupMilliseconds = row.WarmupMilliseconds,
                WarmupRunCount = ToNullableInt(row.WarmupRunCount),
                SampleCount = ToNullableInt(row.SampleCount),
                WarmLatencyP50Milliseconds = row.WarmLatencyP50Milliseconds,
                WarmLatencyP90Milliseconds = row.WarmLatencyP90Milliseconds,
                WarmLatencyP95Milliseconds = row.WarmLatencyP95Milliseconds,
                WarmLatencyP99Milliseconds = row.WarmLatencyP99Milliseconds,
                MeasuredWallTimeMilliseconds = row.MeasuredWallTimeMilliseconds,
                PassesPerSecond = row.PassesPerSecond,
                ThroughputUnitsPerSecond = row.ThroughputUnitsPerSecond,
                AudioDurationSeconds = row.AudioDurationSeconds,
                RealTimeFactorAverage = row.RealTimeFactorAverage,
                ThroughputUnit = ParseEnum<BenchmarkThroughputUnit>(row.ThroughputUnit),
                TimingMode = ParseEnum<BenchmarkTimingMode>(row.TimingMode),
                InputProfileId = row.InputProfileId,
                ComparisonKey = row.ComparisonKey,
                Metadata = BenchmarkRunMetadataSerializer.ToRecordMetadata(
                    BenchmarkRunMetadataSerializer.Deserialize(row.MetadataJson))
            })
            .ToArray();
    }

    private static int? ToNullableInt(long? value) => value is null ? null : (int)value.Value;

    private static TEnum? ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        value is not null && Enum.TryParse(value, ignoreCase: false, out TEnum parsed) && Enum.IsDefined(parsed) ? parsed : null;

    private sealed record BenchmarkRunRow(
        string Id,
        string ModelId,
        string ModelPath,
        string ReportPath,
        string Status,
        string RequestedProvider,
        string SelectedProvider,
        long RunCount,
        long SupportsExecution,
        long ModelSizeBytes,
        double? ColdLoadMilliseconds,
        double? WarmLatencyAverageMilliseconds,
        double? WarmLatencyMinimumMilliseconds,
        double? WarmLatencyMaximumMilliseconds,
        string? FailureReason,
        string GeneratedAtUtc,
        double? FirstRunMilliseconds,
        double? WarmupMilliseconds,
        long? WarmupRunCount,
        long? SampleCount,
        double? WarmLatencyP50Milliseconds,
        double? WarmLatencyP90Milliseconds,
        double? WarmLatencyP95Milliseconds,
        double? WarmLatencyP99Milliseconds,
        double? MeasuredWallTimeMilliseconds,
        double? PassesPerSecond,
        double? ThroughputUnitsPerSecond,
        double? AudioDurationSeconds,
        double? RealTimeFactorAverage,
        string? ThroughputUnit,
        string? TimingMode,
        string? InputProfileId,
        string? ComparisonKey,
        string? MetadataJson);
}
