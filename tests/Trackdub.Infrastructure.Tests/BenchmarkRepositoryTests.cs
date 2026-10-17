using Dapper;
using Microsoft.Data.Sqlite;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace Trackdub.Infrastructure.Tests;

public sealed class BenchmarkRepositoryTests
{
    [Fact]
    public async Task AddAsync_round_trips_all_measurement_columns_and_metadata()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.BenchmarkRepositoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = Path.Join(root, "project.db");
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new Trackdub.Infrastructure.Persistence.Migrations.SqliteDatabaseMigrator(factory);
            await migrator.MigrateAsync(TestContext.Current.CancellationToken);
            var repository = new BenchmarkRepository();
            var metadata = new BenchmarkRunMetadata
            {
                SchemaVersion = 1,
                ComparisonKey = "trt-rtx|fp16",
                Warnings = ["ep-context-miss"],
                Configuration = new BenchmarkConfigurationInfo
                {
                    RequestedProvider = "tensorrt-rtx",
                    TimingMode = BenchmarkTimingMode.ManagedOrtRunEndToEnd,
                    InputProfileId = "synthetic-1s"
                },
                Environment = new BenchmarkEnvironmentInfo { OsDescription = "Windows" }
            };
            var run = new BenchmarkRunRecord(
                Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444"),
                "model-a", @"C:\models\a.onnx", @"C:\reports\a.json",
                BenchmarkStatus.Completed, "auto", "nvtensorrtrtx", 5, true, 2048,
                10.5, 8.1, 7.0, 9.0, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"))
            {
                FirstRunMilliseconds = 11.0,
                WarmupMilliseconds = 2.0,
                WarmupRunCount = 2,
                SampleCount = 5,
                WarmLatencyP50Milliseconds = 8.0,
                WarmLatencyP90Milliseconds = 9.0,
                WarmLatencyP95Milliseconds = 9.5,
                WarmLatencyP99Milliseconds = 9.9,
                MeasuredWallTimeMilliseconds = 50.0,
                PassesPerSecond = 0.1,
                ThroughputUnitsPerSecond = 1.25,
                AudioDurationSeconds = 1.0,
                RealTimeFactorAverage = 0.125,
                ThroughputUnit = (BenchmarkThroughputUnit)0,
                TimingMode = BenchmarkTimingMode.ManagedOrtRunEndToEnd,
                InputProfileId = "synthetic-1s",
                ComparisonKey = "trt-rtx|fp16",
                Metadata = metadata
            };

            await using (System.Data.Common.DbConnection connection = await factory.CreateOpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                await repository.AddAsync(connection, run, cancellationToken: TestContext.Current.CancellationToken);
            }

            await using (System.Data.Common.DbConnection connection = await factory.CreateOpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                BenchmarkRunRecord reloaded = Assert.Single(
                    await repository.ListByModelIdAsync(connection, "model-a", TestContext.Current.CancellationToken));
                Assert.Equal(11.0, reloaded.FirstRunMilliseconds);
                Assert.Equal(2.0, reloaded.WarmupMilliseconds);
                Assert.Equal(2, reloaded.WarmupRunCount);
                Assert.Equal(5, reloaded.SampleCount);
                Assert.Equal(8.0, reloaded.WarmLatencyP50Milliseconds);
                Assert.Equal(9.0, reloaded.WarmLatencyP90Milliseconds);
                Assert.Equal(9.5, reloaded.WarmLatencyP95Milliseconds);
                Assert.Equal(9.9, reloaded.WarmLatencyP99Milliseconds);
                Assert.Equal(50.0, reloaded.MeasuredWallTimeMilliseconds);
                Assert.Equal(0.1, reloaded.PassesPerSecond);
                Assert.Equal(1.25, reloaded.ThroughputUnitsPerSecond);
                Assert.Equal(1.0, reloaded.AudioDurationSeconds);
                Assert.Equal(0.125, reloaded.RealTimeFactorAverage);
                Assert.Equal((BenchmarkThroughputUnit)0, reloaded.ThroughputUnit);
                Assert.Equal(BenchmarkTimingMode.ManagedOrtRunEndToEnd, reloaded.TimingMode);
                Assert.Equal("synthetic-1s", reloaded.InputProfileId);
                Assert.Equal("trt-rtx|fp16", reloaded.ComparisonKey);
                Assert.NotNull(reloaded.Metadata);
                Assert.Equal(1, reloaded.Metadata!.SchemaVersion);
                Assert.Equal("trt-rtx|fp16", reloaded.Metadata.ComparisonKey);
                Assert.Equal("tensorrt-rtx", reloaded.Metadata.Configuration?.RequestedProvider);
                Assert.Equal("synthetic-1s", reloaded.Metadata.Configuration?.InputProfileId);
                Assert.Equal(BenchmarkTimingMode.ManagedOrtRunEndToEnd, reloaded.Metadata.Configuration?.TimingMode);
                Assert.Equal("Windows", reloaded.Metadata.Environment?.OsDescription);
                Assert.Equal(["ep-context-miss"], reloaded.Metadata.Warnings);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddAsync_legacy_row_keeps_new_columns_null()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.BenchmarkRepositoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = Path.Join(root, "project.db");
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new Trackdub.Infrastructure.Persistence.Migrations.SqliteDatabaseMigrator(factory);
            await migrator.MigrateAsync(TestContext.Current.CancellationToken);
            var run = BenchmarkRunRecord.Create("model-b", @"C:\m.onnx", @"C:\r.json",
                BenchmarkStatus.Failed, "auto", "cpu", 1, false, 0,
                null, null, null, null, "boom", DateTimeOffset.Parse("2026-10-17T00:00:00Z"));

            await using (System.Data.Common.DbConnection connection = await factory.CreateOpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                await new BenchmarkRepository().AddAsync(connection, run, cancellationToken: TestContext.Current.CancellationToken);
            }

            await using (System.Data.Common.DbConnection connection = await factory.CreateOpenConnectionAsync(TestContext.Current.CancellationToken))
            {
                BenchmarkRunRecord reloaded = Assert.Single(
                    await new BenchmarkRepository().ListByModelIdAsync(connection, "model-b", TestContext.Current.CancellationToken));
                Assert.Null(reloaded.FirstRunMilliseconds);
                Assert.Null(reloaded.SampleCount);
                Assert.Null(reloaded.Metadata);
                Assert.Null(reloaded.ComparisonKey);
                Assert.Equal(BenchmarkStatus.Failed, reloaded.Status);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }



    [Fact]
    public async Task ListByModelIdAsync_unknown_status_maps_to_failed_and_bad_enums_to_null()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.BenchmarkRepositoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = Path.Join(root, "project.db");
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new Trackdub.Infrastructure.Persistence.Migrations.SqliteDatabaseMigrator(factory);
            var token = TestContext.Current.CancellationToken;
            await migrator.MigrateAsync(token);

            await using (var connection = await factory.CreateOpenConnectionAsync(token))
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO BenchmarkRuns (
                        Id, ModelId, ModelPath, ReportPath, Status, RequestedProvider,
                        SelectedProvider, RunCount, SupportsExecution, ModelSizeBytes,
                        GeneratedAtUtc, ThroughputUnit, TimingMode)
                    VALUES (@Id, 'm-x', '/m.onnx', '/r.json', 'garbage-status', 'auto', 'cpu',
                        1, 0, 0, '2026-10-17T00:00:00+00:00', '999', '999');
                    """,
                    new { Id = Guid.NewGuid().ToString() }, cancellationToken: token));
            }

            await using (var connection = await factory.CreateOpenConnectionAsync(token))
            {
                BenchmarkRunRecord row = Assert.Single(
                    await new BenchmarkRepository().ListByModelIdAsync(connection, "m-x", token));
                Assert.Equal(BenchmarkStatus.Failed, row.Status);
                Assert.Null(row.ThroughputUnit);
                Assert.Null(row.TimingMode);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ListByModelIdAsync_future_schema_metadata_is_unavailable_with_diagnostic()
    {
        string root = Path.Join(Path.GetTempPath(), "Trackdub.BenchmarkRepositoryTests", Guid.NewGuid().ToString("N"));
        try
        {
            string dbPath = Path.Join(root, "project.db");
            var factory = new SqliteConnectionFactory(dbPath);
            var migrator = new Trackdub.Infrastructure.Persistence.Migrations.SqliteDatabaseMigrator(factory);
            var token = TestContext.Current.CancellationToken;
            await migrator.MigrateAsync(token);

            var run = BenchmarkRunRecord.Create("m-future", "/m.onnx", "/r.json",
                BenchmarkStatus.Completed, "auto", "cpu", 1, true, 8,
                1.0, 2.0, 1.0, 3.0, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
            await using (var connection = await factory.CreateOpenConnectionAsync(token))
            {
                await new BenchmarkRepository().AddAsync(connection, run, cancellationToken: token);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE BenchmarkRuns SET MetadataJson = '{\"schemaVersion\":99,\"warnings\":[]}' WHERE ModelId = 'm-future';",
                    cancellationToken: token));
            }

            await using (var connection = await factory.CreateOpenConnectionAsync(token))
            {
                BenchmarkRunRecord row = Assert.Single(
                    await new BenchmarkRepository().ListByModelIdAsync(connection, "m-future", token));
                Assert.Equal(2.0, row.WarmLatencyAverageMilliseconds);
                Assert.Contains(row.Metadata?.Warnings ?? [],
                    w => w.StartsWith("benchmark-metadata-version-unsupported", StringComparison.Ordinal));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
