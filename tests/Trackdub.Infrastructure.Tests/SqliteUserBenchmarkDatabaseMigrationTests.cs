using Microsoft.Data.Sqlite;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Persistence.Migrations;
using Xunit;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteUserBenchmarkDatabaseMigrationTests
{
    private static readonly string[] NewColumns =
    [
        "FirstRunMilliseconds", "WarmupMilliseconds", "WarmupRunCount", "SampleCount",
        "WarmLatencyP50Milliseconds", "WarmLatencyP90Milliseconds", "WarmLatencyP95Milliseconds",
        "WarmLatencyP99Milliseconds", "MeasuredWallTimeMilliseconds", "PassesPerSecond",
        "ThroughputUnitsPerSecond", "AudioDurationSeconds", "RealTimeFactorAverage",
        "ThroughputUnit", "TimingMode", "InputProfileId", "ComparisonKey", "MetadataJson"
    ];

    [Fact]
    public async Task InitializeAsync_fresh_database_has_measurement_columns_and_index()
    {
        string root = TempRoot();
        try
        {
            var database = new SqliteUserBenchmarkDatabase(root);
            var token = TestContext.Current.CancellationToken;
            await database.InitializeAsync(token);
            await database.InitializeAsync(token);

            await using SqliteConnection connection = await database.OpenConnectionAsync(token);
            var columns = await ColumnNamesAsync(connection);
            foreach (string name in NewColumns)
            {
                Assert.Contains(name, columns);
            }

            Assert.Contains("EvidenceId", columns);
            Assert.Contains("FingerprintHash", columns);
            Assert.Contains("Scenario", columns);

            var indexes = new HashSet<string>(StringComparer.Ordinal);
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
                await using var reader = await command.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    indexes.Add(reader.GetString(0));
                }
            }

            Assert.Contains("IX_BenchmarkRuns_ComparisonKey_GeneratedAtUtc", indexes);
            Assert.Contains("IX_BenchmarkRuns_EvidenceId", indexes);
            Assert.Contains("IX_BenchmarkRuns_ModelId", indexes);
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task InitializeAsync_upgrades_legacy_schema_preserving_rows()
    {
        string root = TempRoot();
        try
        {
            var database = new SqliteUserBenchmarkDatabase(root);
            var token = TestContext.Current.CancellationToken;
            Directory.CreateDirectory(root);
            await using (var seed = new SqliteConnection($"Data Source={database.DatabasePath}"))
            {
                await seed.OpenAsync(token);
                await using var command = seed.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE BenchmarkRuns (
                        Id TEXT NOT NULL PRIMARY KEY,
                        ModelId TEXT NOT NULL,
                        ModelPath TEXT NOT NULL,
                        ReportPath TEXT NOT NULL,
                        Status TEXT NOT NULL,
                        RequestedProvider TEXT NOT NULL,
                        SelectedProvider TEXT NOT NULL,
                        RunCount INTEGER NOT NULL,
                        SupportsExecution INTEGER NOT NULL,
                        ModelSizeBytes INTEGER NOT NULL,
                        ColdLoadMilliseconds REAL NULL,
                        WarmLatencyAverageMilliseconds REAL NULL,
                        WarmLatencyMinimumMilliseconds REAL NULL,
                        WarmLatencyMaximumMilliseconds REAL NULL,
                        FailureReason TEXT NULL,
                        GeneratedAtUtc TEXT NOT NULL
                    );
                    INSERT INTO BenchmarkRuns VALUES (
                        '11111111-1111-1111-1111-111111111111', 'legacy-model', '/m.onnx', '/r.json',
                        'Completed', 'auto', 'cpu', 3, 1, 512, 10.0, 8.0, 7.0, 9.0, NULL,
                        '2026-10-01T00:00:00+00:00');
                    """;
                await command.ExecuteNonQueryAsync(token);
            }

            await database.InitializeAsync(token);
            await database.InitializeAsync(token);

            await using SqliteConnection connection = await database.OpenConnectionAsync(token);
            var columns = await ColumnNamesAsync(connection);
            foreach (string name in NewColumns.Concat(["EvidenceId", "FingerprintHash", "Scenario"]))
            {
                Assert.Contains(name, columns);
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "SELECT WarmLatencyAverageMilliseconds, FirstRunMilliseconds, MetadataJson, Scenario FROM BenchmarkRuns;";
                await using var reader = await command.ExecuteReaderAsync(token);
                Assert.True(await reader.ReadAsync(token));
                Assert.Equal(8.0, reader.GetDouble(0));
                Assert.True(await reader.IsDBNullAsync(1, token));
                Assert.True(await reader.IsDBNullAsync(2, token));
                Assert.True(await reader.IsDBNullAsync(3, token));
                Assert.False(await reader.ReadAsync(token));
            }
        }
        finally { Cleanup(root); }
    }

    [Fact]
    public async Task InitializeAsync_concurrent_calls_succeed()
    {
        string root = TempRoot();
        try
        {
            var database = new SqliteUserBenchmarkDatabase(root);
            var token = TestContext.Current.CancellationToken;
            await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => database.InitializeAsync(token)));
        }
        finally { Cleanup(root); }
    }

    private static string TempRoot() =>
        Path.Join(Path.GetTempPath(), "Trackdub.UserBenchMigrationTests", Guid.NewGuid().ToString("N"));

    private static async Task<HashSet<string>> ColumnNamesAsync(SqliteConnection connection)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(BenchmarkRuns);";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(1));
        }

        return names;
    }

    private static void Cleanup(string root)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_distinct_instances_same_root_succeed()
    {
        string root = TempRoot();
        try
        {
            var token = TestContext.Current.CancellationToken;
            await Task.WhenAll(Enumerable.Range(0, 4)
                .Select(_ => new SqliteUserBenchmarkDatabase(root).InitializeAsync(token)));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public async Task Migration_v12_to_v13_preserves_legacy_row_with_null_measurements()
    {
        string root = TempRoot();
        try
        {
            string dbPath = Path.Join(root, "project.db");
            Directory.CreateDirectory(root);
            var token = TestContext.Current.CancellationToken;

            await using (var seed = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}"))
            {
                await seed.OpenAsync(token);
                await using var create = seed.CreateCommand();
                create.CommandText =
                    "CREATE TABLE SchemaVersion (Version INTEGER NOT NULL PRIMARY KEY, Name TEXT NOT NULL, AppliedAtUtc TEXT NOT NULL);";
                await create.ExecuteNonQueryAsync(token);

                foreach (var migration in Trackdub.Infrastructure.Persistence.Migrations.SqliteMigrations.All
                    .Where(m => m.Version <= 12).OrderBy(m => m.Version))
                {
                    await using var command = seed.CreateCommand();
                    command.CommandText = migration.Sql;
                    await command.ExecuteNonQueryAsync(token);
                    await using var mark = seed.CreateCommand();
                    mark.CommandText = "INSERT INTO SchemaVersion VALUES ($v, $n, '2026-10-01T00:00:00+00:00');";
                    mark.Parameters.AddWithValue("$v", migration.Version);
                    mark.Parameters.AddWithValue("$n", migration.Name);
                    await mark.ExecuteNonQueryAsync(token);
                }

                await using var insert = seed.CreateCommand();
                insert.CommandText =
                    """
                    INSERT INTO BenchmarkRuns (
                        Id, ModelId, ModelPath, ReportPath, Status, RequestedProvider,
                        SelectedProvider, RunCount, SupportsExecution, ModelSizeBytes,
                        ColdLoadMilliseconds, WarmLatencyAverageMilliseconds,
                        WarmLatencyMinimumMilliseconds, WarmLatencyMaximumMilliseconds,
                        FailureReason, GeneratedAtUtc)
                    VALUES (
                        '33333333-3333-3333-3333-333333333333', 'legacy-m', '/m.onnx', '/r.json',
                        'Completed', 'auto', 'cpu', 3, 1, 128, 10.0, 8.0, 7.0, 9.0, NULL,
                        '2026-10-01T00:00:00+00:00');
                    """;
                await insert.ExecuteNonQueryAsync(token);
            }

            var migrator = new Trackdub.Infrastructure.Persistence.Migrations.SqliteDatabaseMigrator(
                new SqliteConnectionFactory(dbPath));
            await migrator.MigrateAsync(token);

            var repository = new Trackdub.Infrastructure.Persistence.Repositories.BenchmarkRepository();
            await using var connection = await new SqliteConnectionFactory(dbPath).CreateOpenConnectionAsync(token);
            Trackdub.Domain.BenchmarkRunRecord row = Assert.Single(
                await repository.ListByModelIdAsync(connection, "legacy-m", token));
            Assert.Equal(8.0, row.WarmLatencyAverageMilliseconds);
            Assert.Null(row.FirstRunMilliseconds);
            Assert.Null(row.Metadata);

            await using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(1) FROM SchemaVersion WHERE Version = 13;";
                Assert.Equal(1L, await check.ExecuteScalarAsync(token));
            }
        }
        finally
        {
            Cleanup(root);
        }
    }
}
