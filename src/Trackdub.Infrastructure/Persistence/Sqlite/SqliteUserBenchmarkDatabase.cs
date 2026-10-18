using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteUserBenchmarkDatabase
{
    public const string DatabaseFileName = "benchmarks.db";
    private const string BenchmarkRunsTable = "BenchmarkRuns";

    private readonly string databasePath;

    public SqliteUserBenchmarkDatabase(string userDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        databasePath = Path.Join(Path.GetFullPath(userDataRoot), DatabaseFileName);
    }

    public string DatabasePath => databasePath;

    private static readonly (string Name, string Type)[] LegacyOptionalColumns =
    [
        ("EvidenceId", "TEXT"),
        ("FingerprintHash", "TEXT"),
        ("Scenario", "TEXT")
    ];

    private static readonly (string Name, string Type)[] MeasurementColumns =
    [
        ("FirstRunMilliseconds", "REAL"),
        ("WarmupMilliseconds", "REAL"),
        ("WarmupRunCount", "INTEGER"),
        ("SampleCount", "INTEGER"),
        ("WarmLatencyP50Milliseconds", "REAL"),
        ("WarmLatencyP90Milliseconds", "REAL"),
        ("WarmLatencyP95Milliseconds", "REAL"),
        ("WarmLatencyP99Milliseconds", "REAL"),
        ("MeasuredWallTimeMilliseconds", "REAL"),
        ("PassesPerSecond", "REAL"),
        ("ThroughputUnitsPerSecond", "REAL"),
        ("AudioDurationSeconds", "REAL"),
        ("RealTimeFactorAverage", "REAL"),
        ("ThroughputUnit", "TEXT"),
        ("TimingMode", "TEXT"),
        ("InputProfileId", "TEXT"),
        ("ComparisonKey", "TEXT"),
        ("MetadataJson", "TEXT")
    ];

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> AllowedColumnDefinitions =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal)
        {
            [BenchmarkRunsTable] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["EvidenceId"] = "TEXT",
                ["FingerprintHash"] = "TEXT",
                ["Scenario"] = "TEXT",
                ["FirstRunMilliseconds"] = "REAL",
                ["WarmupMilliseconds"] = "REAL",
                ["WarmupRunCount"] = "INTEGER",
                ["SampleCount"] = "INTEGER",
                ["WarmLatencyP50Milliseconds"] = "REAL",
                ["WarmLatencyP90Milliseconds"] = "REAL",
                ["WarmLatencyP95Milliseconds"] = "REAL",
                ["WarmLatencyP99Milliseconds"] = "REAL",
                ["MeasuredWallTimeMilliseconds"] = "REAL",
                ["PassesPerSecond"] = "REAL",
                ["ThroughputUnitsPerSecond"] = "REAL",
                ["AudioDurationSeconds"] = "REAL",
                ["RealTimeFactorAverage"] = "REAL",
                ["ThroughputUnit"] = "TEXT",
                ["TimingMode"] = "TEXT",
                ["InputProfileId"] = "TEXT",
                ["ComparisonKey"] = "TEXT",
                ["MetadataJson"] = "TEXT"
            }
        };

    private readonly SemaphoreSlim initializationGate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await ExecuteAsync(connection, transaction,
                    """
                    CREATE TABLE IF NOT EXISTS BenchmarkRuns (
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
                        GeneratedAtUtc TEXT NOT NULL,
                        EvidenceId TEXT NULL,
                        FingerprintHash TEXT NULL,
                        Scenario TEXT NULL,
                        FirstRunMilliseconds REAL NULL,
                        WarmupMilliseconds REAL NULL,
                        WarmupRunCount INTEGER NULL,
                        SampleCount INTEGER NULL,
                        WarmLatencyP50Milliseconds REAL NULL,
                        WarmLatencyP90Milliseconds REAL NULL,
                        WarmLatencyP95Milliseconds REAL NULL,
                        WarmLatencyP99Milliseconds REAL NULL,
                        MeasuredWallTimeMilliseconds REAL NULL,
                        PassesPerSecond REAL NULL,
                        ThroughputUnitsPerSecond REAL NULL,
                        AudioDurationSeconds REAL NULL,
                        RealTimeFactorAverage REAL NULL,
                        ThroughputUnit TEXT NULL,
                        TimingMode TEXT NULL,
                        InputProfileId TEXT NULL,
                        ComparisonKey TEXT NULL,
                        MetadataJson TEXT NULL
                    );

                    CREATE TABLE IF NOT EXISTS BenchmarkEvidenceReports (
                        RunId TEXT NOT NULL PRIMARY KEY,
                        Kind TEXT NOT NULL,
                        CompletedAtUtc TEXT NOT NULL,
                        ReportFileName TEXT NOT NULL
                    );
                    """, cancellationToken).ConfigureAwait(false);

                await EnsureColumnsAsync(connection, transaction, "BenchmarkRuns", LegacyOptionalColumns, cancellationToken).ConfigureAwait(false);
                await EnsureColumnsAsync(connection, transaction, "BenchmarkRuns", MeasurementColumns, cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection, transaction,
                    """
                    CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_ModelId
                        ON BenchmarkRuns (ModelId);

                    CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_EvidenceId
                        ON BenchmarkRuns (EvidenceId, Scenario);

                    CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_ComparisonKey_GeneratedAtUtc
                        ON BenchmarkRuns (ComparisonKey, GeneratedAtUtc);

                    CREATE INDEX IF NOT EXISTS IX_BenchmarkEvidenceReports_Recent
                        ON BenchmarkEvidenceReports (Kind, CompletedAtUtc DESC);
                    """, cancellationToken).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            initializationGate.Release();
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static bool IsAllowedColumnDefinition(string tableName, string name, string type) =>
        AllowedColumnDefinitions.TryGetValue(tableName, out IReadOnlyDictionary<string, string>? columns)
        && columns.TryGetValue(name, out string? allowedType)
        && string.Equals(type, allowedType, StringComparison.Ordinal);

    private static async Task EnsureColumnsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        (string Name, string Type)[] columns,
        CancellationToken cancellationToken)
    {
        if (!AllowedColumnDefinitions.TryGetValue(tableName, out IReadOnlyDictionary<string, string>? allowedColumns))
        {
            throw new ArgumentException("SQLite table is not in the schema migration allowlist.", nameof(tableName));
        }

        var safeColumns = new (string Name, string Type)[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            (string name, string type) = columns[i];
            if (!allowedColumns.TryGetValue(name, out string? allowedType)
                || !string.Equals(type, allowedType, StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "SQLite column definition is not in the schema migration allowlist.",
                    nameof(columns));
            }

            safeColumns[i] = (name, allowedType);
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = $"PRAGMA table_info(\"{tableName}\");";
            await using var reader = await pragma.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(1));
            }
        }

        foreach ((string name, string type) in safeColumns)
        {
            if (existing.Contains(name))
            {
                continue;
            }

            await ExecuteAsync(connection, transaction,
                $"ALTER TABLE \"{tableName}\" ADD COLUMN \"{name}\" {type} NULL;", cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteProjectDatabase.EnableForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
