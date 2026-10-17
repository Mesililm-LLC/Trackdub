using Dapper;
using Microsoft.Data.Sqlite;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace Trackdub.Infrastructure.Tests;

public sealed class UserBenchmarkRepositoryTests
{
    [Fact]
    public async Task AddAsync_round_trips_profiler_runs_by_evidence_id()
    {
        string userRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new SqliteUserBenchmarkDatabase(userRoot);
            var repository = new UserBenchmarkRepository(database);
            Guid evidenceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            string modelId = UserBenchmarkRepository.BuildProfilerModelId(evidenceId, "abc123", "vad");
            DateTimeOffset generatedAt = DateTimeOffset.Parse("2026-06-12T12:00:00Z");

            var run = new BenchmarkRunRecord(
                Guid.Parse("99999999-8888-7777-6666-555555555555"),
                modelId,
                @"C:\models\vad.onnx",
                @"C:\reports\vad-report.json",
                BenchmarkStatus.Completed,
                "auto",
                "dml",
                3,
                true,
                1024,
                120.5,
                40.1,
                38.0,
                44.0,
                null,
                generatedAt);

            await repository.AddAsync(run, TestContext.Current.CancellationToken);

            Assert.True(await repository.ContainsEvidenceAsync(evidenceId, TestContext.Current.CancellationToken));

            IReadOnlyList<BenchmarkRunRecord> byEvidence =
                await repository.ListByEvidenceIdAsync(evidenceId, TestContext.Current.CancellationToken);
            BenchmarkRunRecord reloaded = Assert.Single(byEvidence);
            Assert.Equal(run.Id, reloaded.Id);
            Assert.Equal(modelId, reloaded.ModelId);
            Assert.Equal(40.1, reloaded.WarmLatencyAverageMilliseconds);

            IReadOnlyList<BenchmarkRunRecord> recent =
                await repository.ListRecentAsync(5, TestContext.Current.CancellationToken);
            Assert.Contains(recent, item => item.Id == run.Id);

            await repository.AddAsync(run with { WarmLatencyAverageMilliseconds = 41.0 }, TestContext.Current.CancellationToken);
            BenchmarkRunRecord updated = Assert.Single(
                await repository.ListByEvidenceIdAsync(evidenceId, TestContext.Current.CancellationToken));
            Assert.Equal(41.0, updated.WarmLatencyAverageMilliseconds);
        }
        finally
        {
            string dbPath = Path.Join(userRoot, SqliteUserBenchmarkDatabase.DatabaseFileName);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            if (Directory.Exists(userRoot))
            {
                Directory.Delete(userRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ParseProfilerMetadata_ReturnsNullForNonProfilerModelIds()
    {
        (Guid? evidenceId, string? fingerprintHash, string? scenario) =
            UserBenchmarkRepository.ParseProfilerMetadata("whisper-large-v3");

        Assert.Null(evidenceId);
        Assert.Null(fingerprintHash);
        Assert.Null(scenario);
    }
    [Fact]
    public async Task ListRecentAsync_maps_unknown_status_to_failed()
    {
        string userRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new SqliteUserBenchmarkDatabase(userRoot);
            var repository = new UserBenchmarkRepository(database);
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            Guid runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            await connection.ExecuteAsync(
                """
                INSERT INTO BenchmarkRuns (
                    Id, ModelId, ModelPath, ReportPath, Status, RequestedProvider, SelectedProvider,
                    RunCount, SupportsExecution, ModelSizeBytes, GeneratedAtUtc)
                VALUES (
                    @Id, @ModelId, @ModelPath, @ReportPath, @Status, @RequestedProvider, @SelectedProvider,
                    @RunCount, @SupportsExecution, @ModelSizeBytes, @GeneratedAtUtc)
                """,
                new
                {
                    Id = runId.ToString("D"),
                    ModelId = "whisper-large-v3",
                    ModelPath = "model.onnx",
                    ReportPath = "report.json",
                    Status = "corrupted-status",
                    RequestedProvider = "auto",
                    SelectedProvider = "cpu",
                    RunCount = 1,
                    SupportsExecution = 0,
                    ModelSizeBytes = 0L,
                    GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                });

            BenchmarkRunRecord reloaded = Assert.Single(
                await repository.ListRecentAsync(1, TestContext.Current.CancellationToken));

            Assert.Equal(runId, reloaded.Id);
            Assert.Equal(BenchmarkStatus.Failed, reloaded.Status);
        }
        finally
        {
            if (Directory.Exists(userRoot))
            {
                Directory.Delete(userRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AddAsync_round_trips_measurement_columns_and_metadata()
    {
        string userRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new UserBenchmarkRepository(new SqliteUserBenchmarkDatabase(userRoot));
            var token = TestContext.Current.CancellationToken;
            var metadata = new BenchmarkRunMetadata
            {
                SchemaVersion = 1,
                ComparisonKey = "dml|fp16",
                Warnings = ["sampling-gap"],
                Configuration = new BenchmarkConfigurationInfo
                {
                    RequestedProvider = "dml",
                    TimingMode = BenchmarkTimingMode.ManagedOrtRunEndToEnd,
                    InputProfileId = "synthetic-v1"
                }
            };
            var run = new BenchmarkRunRecord(
                Guid.Parse("22222222-2222-2222-2222-222222222222"), "model-u", "/m.onnx", "/r.json",
                BenchmarkStatus.Completed, "auto", "dml", 4, true, 64,
                1.0, 2.0, 1.5, 2.5, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"))
            {
                FirstRunMilliseconds = 10.0, WarmupMilliseconds = 200.0, WarmupRunCount = 1, SampleCount = 4,
                WarmLatencyP50Milliseconds = 25.0, WarmLatencyP90Milliseconds = 37.0,
                WarmLatencyP95Milliseconds = 38.5, WarmLatencyP99Milliseconds = 39.7,
                MeasuredWallTimeMilliseconds = 200.0, PassesPerSecond = 20.0,
                ThroughputUnitsPerSecond = 600.0, AudioDurationSeconds = 30.0,
                RealTimeFactorAverage = 25.0 / 30000.0, ThroughputUnit = BenchmarkThroughputUnit.AudioSecondsPerSecond,
                TimingMode = BenchmarkTimingMode.ManagedOrtRunEndToEnd,
                InputProfileId = "synthetic-v1", ComparisonKey = "dml|fp16", Metadata = metadata
            };

            await repository.AddAsync(run, token);
            BenchmarkRunRecord reloaded = Assert.Single(await repository.ListRecentAsync(1, token));
            Assert.Equal(10.0, reloaded.FirstRunMilliseconds);
            Assert.Equal(200.0, reloaded.WarmupMilliseconds);
            Assert.Equal(1, reloaded.WarmupRunCount);
            Assert.Equal(4, reloaded.SampleCount);
            Assert.Equal(25.0, reloaded.WarmLatencyP50Milliseconds);
            Assert.Equal(37.0, reloaded.WarmLatencyP90Milliseconds);
            Assert.Equal(38.5, reloaded.WarmLatencyP95Milliseconds);
            Assert.Equal(39.7, reloaded.WarmLatencyP99Milliseconds);
            Assert.Equal(200.0, reloaded.MeasuredWallTimeMilliseconds);
            Assert.Equal(20.0, reloaded.PassesPerSecond);
            Assert.Equal(600.0, reloaded.ThroughputUnitsPerSecond);
            Assert.Equal(30.0, reloaded.AudioDurationSeconds);
            Assert.Equal(25.0 / 30000.0, reloaded.RealTimeFactorAverage);
            Assert.Equal(BenchmarkThroughputUnit.AudioSecondsPerSecond, reloaded.ThroughputUnit);
            Assert.Equal(BenchmarkTimingMode.ManagedOrtRunEndToEnd, reloaded.TimingMode);
            Assert.Equal("synthetic-v1", reloaded.InputProfileId);
            Assert.Equal("dml|fp16", reloaded.ComparisonKey);
            Assert.Equal("synthetic-v1", reloaded.Metadata?.Configuration?.InputProfileId);
            Assert.Equal(["sampling-gap"], reloaded.Metadata?.Warnings);

            await repository.AddAsync(run with { SampleCount = 7, Metadata = null }, token);
            BenchmarkRunRecord updated = Assert.Single(await repository.ListRecentAsync(1, token));
            Assert.Equal(7, updated.SampleCount);
            Assert.Null(updated.Metadata);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(userRoot)) Directory.Delete(userRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ListRecentAsync_corrupt_metadata_returns_scalars_and_diagnostic()
    {
        string userRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new UserBenchmarkRepository(new SqliteUserBenchmarkDatabase(userRoot));
            var token = TestContext.Current.CancellationToken;
            var run = BenchmarkRunRecord.Create("model-c", "/m.onnx", "/r.json",
                BenchmarkStatus.Completed, "auto", "cpu", 1, true, 8,
                1.0, 2.0, 1.0, 3.0, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
            await repository.AddAsync(run, token);

            var database = new SqliteUserBenchmarkDatabase(userRoot);
            await using var connection = await database.OpenConnectionAsync(token);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE BenchmarkRuns SET MetadataJson = '{not-json' WHERE Id = @Id;",
                new { Id = run.Id.ToString() }, cancellationToken: token));

            BenchmarkRunRecord reloaded = Assert.Single(await repository.ListRecentAsync(1, token));
            Assert.Equal(2.0, reloaded.WarmLatencyAverageMilliseconds);
            Assert.Contains(reloaded.Metadata?.Warnings ?? [],
                warning => warning.StartsWith("benchmark-metadata-unreadable", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(userRoot)) Directory.Delete(userRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ListRecentAsync_unknown_status_numeric_enums_and_future_schema()
    {
        string userRoot = Path.Join(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var repository = new UserBenchmarkRepository(new SqliteUserBenchmarkDatabase(userRoot));
            var token = TestContext.Current.CancellationToken;
            var run = BenchmarkRunRecord.Create("model-z", "/m.onnx", "/r.json",
                BenchmarkStatus.Completed, "auto", "cpu", 1, true, 8,
                1.0, 2.0, 1.0, 3.0, null, DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
            await repository.AddAsync(run, token);

            var database = new SqliteUserBenchmarkDatabase(userRoot);
            await using var connection = await database.OpenConnectionAsync(token);
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE BenchmarkRuns SET Status = 'not-a-status', ThroughputUnit = '999', TimingMode = '999', MetadataJson = '{\"schemaVersion\":42}' WHERE Id = @Id;",
                new { Id = run.Id.ToString() }, cancellationToken: token));

            BenchmarkRunRecord reloaded = Assert.Single(await repository.ListRecentAsync(1, token));
            Assert.Equal(BenchmarkStatus.Failed, reloaded.Status);
            Assert.Null(reloaded.ThroughputUnit);
            Assert.Null(reloaded.TimingMode);
            Assert.Equal(2.0, reloaded.WarmLatencyAverageMilliseconds);
            Assert.Contains(reloaded.Metadata?.Warnings ?? [],
                w => w.StartsWith("benchmark-metadata-version-unsupported", StringComparison.Ordinal));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(userRoot))
            {
                Directory.Delete(userRoot, recursive: true);
            }
        }
    }
}
