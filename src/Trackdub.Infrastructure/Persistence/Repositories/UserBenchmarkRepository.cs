using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class UserBenchmarkRepository(SqliteUserBenchmarkDatabase database) : IUserBenchmarkRepository
{
    public async Task AddAsync(BenchmarkRunRecord run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        (Guid? evidenceId, string? fingerprintHash, string? scenario) = ParseProfilerMetadata(run.ModelId);

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
                EvidenceId,
                FingerprintHash,
                Scenario,
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
                @EvidenceId,
                @FingerprintHash,
                @Scenario,
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
                @MetadataJson)
            ON CONFLICT(Id) DO UPDATE SET
                ModelId = excluded.ModelId,
                ModelPath = excluded.ModelPath,
                ReportPath = excluded.ReportPath,
                Status = excluded.Status,
                RequestedProvider = excluded.RequestedProvider,
                SelectedProvider = excluded.SelectedProvider,
                RunCount = excluded.RunCount,
                SupportsExecution = excluded.SupportsExecution,
                ModelSizeBytes = excluded.ModelSizeBytes,
                ColdLoadMilliseconds = excluded.ColdLoadMilliseconds,
                WarmLatencyAverageMilliseconds = excluded.WarmLatencyAverageMilliseconds,
                WarmLatencyMinimumMilliseconds = excluded.WarmLatencyMinimumMilliseconds,
                WarmLatencyMaximumMilliseconds = excluded.WarmLatencyMaximumMilliseconds,
                FailureReason = excluded.FailureReason,
                GeneratedAtUtc = excluded.GeneratedAtUtc,
                EvidenceId = excluded.EvidenceId,
                FingerprintHash = excluded.FingerprintHash,
                Scenario = excluded.Scenario,
                FirstRunMilliseconds = excluded.FirstRunMilliseconds,
                WarmupMilliseconds = excluded.WarmupMilliseconds,
                WarmupRunCount = excluded.WarmupRunCount,
                SampleCount = excluded.SampleCount,
                WarmLatencyP50Milliseconds = excluded.WarmLatencyP50Milliseconds,
                WarmLatencyP90Milliseconds = excluded.WarmLatencyP90Milliseconds,
                WarmLatencyP95Milliseconds = excluded.WarmLatencyP95Milliseconds,
                WarmLatencyP99Milliseconds = excluded.WarmLatencyP99Milliseconds,
                MeasuredWallTimeMilliseconds = excluded.MeasuredWallTimeMilliseconds,
                PassesPerSecond = excluded.PassesPerSecond,
                ThroughputUnitsPerSecond = excluded.ThroughputUnitsPerSecond,
                AudioDurationSeconds = excluded.AudioDurationSeconds,
                RealTimeFactorAverage = excluded.RealTimeFactorAverage,
                ThroughputUnit = excluded.ThroughputUnit,
                TimingMode = excluded.TimingMode,
                InputProfileId = excluded.InputProfileId,
                ComparisonKey = excluded.ComparisonKey,
                MetadataJson = excluded.MetadataJson;
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
                EvidenceId = evidenceId is null ? null : SqliteValueConverters.ToDbValue(evidenceId.Value),
                FingerprintHash = fingerprintHash,
                Scenario = scenario,
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
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<bool> ContainsEvidenceAsync(Guid evidenceId, CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        int count = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            """
            SELECT COUNT(1)
            FROM BenchmarkRuns
            WHERE EvidenceId = @EvidenceId;
            """,
            new { EvidenceId = SqliteValueConverters.ToDbValue(evidenceId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return count > 0;
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListByEvidenceIdAsync(
        Guid evidenceId,
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

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
            WHERE EvidenceId = @EvidenceId
            ORDER BY Scenario, GeneratedAtUtc;
            """,
            new { EvidenceId = SqliteValueConverters.ToDbValue(evidenceId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows.Select(MapRow).ToArray();
    }

    public async Task<IReadOnlyList<BenchmarkRunRecord>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            return [];
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

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
            ORDER BY GeneratedAtUtc DESC, Id DESC
            LIMIT @Limit;
            """,
            new { Limit = limit },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows.Select(MapRow).ToArray();
    }

    internal static (Guid? EvidenceId, string? FingerprintHash, string? Scenario) ParseProfilerMetadata(string modelId)
    {
        const string prefix = "hardware-profiler:";
        if (!modelId.StartsWith(prefix, StringComparison.Ordinal))
        {
            return (null, null, null);
        }

        string[] parts = modelId[prefix.Length..].Split(':', 3);
        if (parts.Length < 3)
        {
            return (null, null, null);
        }

        return Guid.TryParse(parts[0], out Guid evidenceId)
            ? (evidenceId, parts[1], parts[2])
            : (null, null, null);
    }

    internal static string BuildProfilerModelId(Guid evidenceId, string fingerprintHash, string scenarioName) =>
        $"hardware-profiler:{evidenceId:D}:{fingerprintHash}:{scenarioName}";

    private static BenchmarkRunRecord MapRow(BenchmarkRunRow row)
    {
        BenchmarkRunMetadataReadResult metadata = BenchmarkRunMetadataSerializer.Deserialize(row.MetadataJson);
        return new BenchmarkRunRecord(
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
            Metadata = BenchmarkRunMetadataSerializer.ToRecordMetadata(metadata)
        };
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
