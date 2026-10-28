using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Speakers;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteSpeakerRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : ISpeakerRepository
{
    public async Task<IReadOnlyList<ProjectSpeaker>> ListSpeakersAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return [];
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   display_name,
                   created_at_utc
            FROM speakers
            WHERE project_id = $projectId AND merged_into_speaker_id IS NULL
            ORDER BY created_at_utc, display_name;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var speakers = new List<ProjectSpeaker>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            speakers.Add(new ProjectSpeaker(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetString(2),
                new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc))));
        }

        return speakers;
    }

    public async Task<IReadOnlyList<SpeakerTurn>> ListTurnsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return [];
        }

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   speaker_id,
                   stage_run_id,
                   start_seconds,
                   end_seconds,
                   confidence,
                   has_overlap
            FROM speaker_turns
            WHERE project_id = $projectId
            ORDER BY start_seconds, end_seconds, id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var turns = new List<SpeakerTurn>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            turns.Add(new SpeakerTurn(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.GetInt64(7) == 1,
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3))));
        }

        return turns;
    }

    public async Task<ProjectSpeaker> EnsureDefaultSpeakerAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;

        ProjectSpeaker? first = await GetFirstSpeakerAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        if (first is not null)
        {
            return first;
        }

        ProjectSpeaker speaker = ProjectSpeaker.Create(projectId, "Speaker 1", DateTimeOffset.UtcNow);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO speakers (
                id,
                project_id,
                display_name,
                created_at_utc)
            SELECT
                $id,
                $projectId,
                $displayName,
                $createdAtUtc
            WHERE NOT EXISTS (
                SELECT 1 FROM speakers
                WHERE project_id = $projectId AND merged_into_speaker_id IS NULL
            );
            """;
        BindSpeaker(command, speaker);
        int inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (inserted == 0)
        {
            ProjectSpeaker? fallback = await GetFirstSpeakerAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
            if (fallback is null)
            {
                throw new InvalidOperationException("Unable to create or retrieve a default speaker.");
            }

            return fallback;
        }

        return speaker;
    }

    public async Task<ProjectSpeaker> CreateSpeakerAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;

        IReadOnlyList<string> displayNames = await ListSpeakerDisplayNamesAsync(connection, projectId, cancellationToken).ConfigureAwait(false);
        ProjectSpeaker speaker = ProjectSpeaker.Create(
            projectId,
            BuildNextSpeakerDisplayName(displayNames),
            DateTimeOffset.UtcNow);

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO speakers (
                id,
                project_id,
                display_name,
                created_at_utc)
            VALUES (
                $id,
                $projectId,
                $displayName,
                $createdAtUtc);
            """;
        BindSpeaker(command, speaker);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return speaker;
    }

    public async Task ReplaceDiarizationAsync(
        Guid projectId,
        IReadOnlyList<ProjectSpeaker> speakers,
        IReadOnlyList<SpeakerTurn> turns,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(speakers);
        ArgumentNullException.ThrowIfNull(turns);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Referenced speaker identities and voice assignments outlive diarization turns.
        // New model labels are inserted alongside them so a rerun never deletes user work.
        await using (SqliteCommand clearTurns = connection.CreateCommand())
        {
            clearTurns.Transaction = transaction;
            clearTurns.CommandText = "DELETE FROM speaker_turns WHERE project_id = $projectId;";
            clearTurns.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
            await clearTurns.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand insertSpeaker = connection.CreateCommand())
        {
            insertSpeaker.Transaction = transaction;
            insertSpeaker.CommandText =
                """
                INSERT INTO speakers (
                    id,
                    project_id,
                    display_name,
                    created_at_utc)
                VALUES (
                    $id,
                    $projectId,
                    $displayName,
                    $createdAtUtc);
                """;
            SqliteParameter idParameter = insertSpeaker.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter projectIdParameter = insertSpeaker.Parameters.Add("$projectId", SqliteType.Text);
            SqliteParameter displayNameParameter = insertSpeaker.Parameters.Add("$displayName", SqliteType.Text);
            SqliteParameter createdAtUtcParameter = insertSpeaker.Parameters.Add("$createdAtUtc", SqliteType.Text);

            foreach (ProjectSpeaker speaker in speakers)
            {
                idParameter.Value = speaker.Id.ToString("D");
                projectIdParameter.Value = speaker.ProjectId.ToString("D");
                displayNameParameter.Value = speaker.DisplayName;
                createdAtUtcParameter.Value = speaker.CreatedAtUtc.UtcDateTime;
                await insertSpeaker.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (SqliteCommand insertTurn = connection.CreateCommand())
        {
            insertTurn.Transaction = transaction;
            insertTurn.CommandText =
                """
                INSERT INTO speaker_turns (
                    id,
                    project_id,
                    speaker_id,
                    stage_run_id,
                    start_seconds,
                    end_seconds,
                    confidence,
                    has_overlap)
                VALUES (
                    $id,
                    $projectId,
                    $speakerId,
                    $stageRunId,
                    $startSeconds,
                    $endSeconds,
                    $confidence,
                    $hasOverlap);
                """;
            SqliteParameter idParameter = insertTurn.Parameters.Add("$id", SqliteType.Text);
            SqliteParameter projectIdParameter = insertTurn.Parameters.Add("$projectId", SqliteType.Text);
            SqliteParameter speakerIdParameter = insertTurn.Parameters.Add("$speakerId", SqliteType.Text);
            SqliteParameter stageRunIdParameter = insertTurn.Parameters.Add("$stageRunId", SqliteType.Text);
            SqliteParameter startSecondsParameter = insertTurn.Parameters.Add("$startSeconds", SqliteType.Real);
            SqliteParameter endSecondsParameter = insertTurn.Parameters.Add("$endSeconds", SqliteType.Real);
            SqliteParameter confidenceParameter = insertTurn.Parameters.Add("$confidence", SqliteType.Real);
            SqliteParameter hasOverlapParameter = insertTurn.Parameters.Add("$hasOverlap", SqliteType.Integer);

            foreach (SpeakerTurn turn in turns.OrderBy(static candidate => candidate.StartSeconds))
            {
                idParameter.Value = turn.Id.ToString("D");
                projectIdParameter.Value = turn.ProjectId.ToString("D");
                speakerIdParameter.Value = turn.SpeakerId.ToString("D");
                stageRunIdParameter.Value = turn.StageRunId?.ToString("D") ?? (object)DBNull.Value;
                startSecondsParameter.Value = turn.StartSeconds;
                endSecondsParameter.Value = turn.EndSeconds;
                confidenceParameter.Value = turn.Confidence ?? (object)DBNull.Value;
                hasOverlapParameter.Value = turn.HasOverlap ? 1 : 0;
                await insertTurn.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameSpeakerAsync(
        Guid projectId,
        Guid speakerId,
        string displayName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE speakers
            SET display_name = $displayName
            WHERE project_id = $projectId
              AND id = $speakerId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$speakerId", speakerId.ToString("D"));
        command.Parameters.AddWithValue("$displayName", displayName.Trim());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task MergeSpeakersAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken)
        => new SqliteTranscriptRepository(database, scopedConnectionProvider)
            .ReassignAndMergeSpeakersAsync(projectId, sourceSpeakerId, targetSpeakerId, cancellationToken);

    public async Task SplitTurnAsync(
        Guid projectId,
        Guid speakerTurnId,
        double splitSeconds,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        SpeakerTurn turn = await GetRequiredTurnAsync(connection, transaction, projectId, speakerTurnId, cancellationToken).ConfigureAwait(false);
        if (!double.IsFinite(splitSeconds) || splitSeconds <= turn.StartSeconds || splitSeconds >= turn.EndSeconds)
        {
            throw new InvalidOperationException("Split time must fall inside the selected speaker turn.");
        }

        await using (SqliteCommand deleteCommand = connection.CreateCommand())
        {
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText =
                """
                DELETE FROM speaker_turns
                WHERE project_id = $projectId
                  AND id = $speakerTurnId;
                """;
            deleteCommand.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
            deleteCommand.Parameters.AddWithValue("$speakerTurnId", speakerTurnId.ToString("D"));
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        SpeakerTurn left = SpeakerTurn.Create(
            projectId,
            turn.SpeakerId,
            turn.StartSeconds,
            splitSeconds,
            turn.Confidence,
            turn.HasOverlap,
            turn.StageRunId);
        SpeakerTurn right = SpeakerTurn.Create(
            projectId,
            turn.SpeakerId,
            splitSeconds,
            turn.EndSeconds,
            turn.Confidence,
            turn.HasOverlap,
            turn.StageRunId);

        await InsertTurnAsync(connection, transaction, left, cancellationToken).ConfigureAwait(false);
        await InsertTurnAsync(connection, transaction, right, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void BindSpeaker(SqliteCommand command, ProjectSpeaker speaker)
    {
        command.Parameters.AddWithValue("$id", speaker.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", speaker.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$displayName", speaker.DisplayName);
        command.Parameters.AddWithValue("$createdAtUtc", speaker.CreatedAtUtc.UtcDateTime);
    }

    private static async Task<IReadOnlyList<string>> ListSpeakerDisplayNamesAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT display_name
            FROM speakers
            WHERE project_id = $projectId AND merged_into_speaker_id IS NULL;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var names = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static string BuildNextSpeakerDisplayName(IReadOnlyList<string> existingDisplayNames)
    {
        const string prefix = "Speaker ";
        HashSet<string> names = existingDisplayNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int nextNumber = existingDisplayNames
            .Select(name => name.Trim())
            .Where(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => int.TryParse(name[prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max() + 1;

        string candidate;
        do
        {
            candidate = $"{prefix}{nextNumber++}";
        }
        while (names.Contains(candidate));

        return candidate;
    }

    private static async Task<SpeakerTurn> GetRequiredTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        Guid speakerTurnId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   speaker_id,
                   stage_run_id,
                   start_seconds,
                   end_seconds,
                   confidence,
                   has_overlap
            FROM speaker_turns
            WHERE project_id = $projectId
              AND id = $speakerTurnId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$speakerTurnId", speakerTurnId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The selected speaker turn was not found.");
        }

        return new SpeakerTurn(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetDouble(4),
            reader.GetDouble(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.GetInt64(7) == 1,
            reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)));
    }

    private static async Task<ProjectSpeaker?> GetFirstSpeakerAsync(
        SqliteConnection connection,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   display_name,
                   created_at_utc
            FROM speakers
            WHERE project_id = $projectId AND merged_into_speaker_id IS NULL
            ORDER BY created_at_utc, display_name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProjectSpeaker(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
    }

    private static async Task InsertTurnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SpeakerTurn turn,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO speaker_turns (
                id,
                project_id,
                speaker_id,
                stage_run_id,
                start_seconds,
                end_seconds,
                confidence,
                has_overlap)
            VALUES (
                $id,
                $projectId,
                $speakerId,
                $stageRunId,
                $startSeconds,
                $endSeconds,
                $confidence,
                $hasOverlap);
            """;
        command.Parameters.AddWithValue("$id", turn.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", turn.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$speakerId", turn.SpeakerId.ToString("D"));
        command.Parameters.AddWithValue("$stageRunId", turn.StageRunId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$startSeconds", turn.StartSeconds);
        command.Parameters.AddWithValue("$endSeconds", turn.EndSeconds);
        command.Parameters.AddWithValue("$confidence", turn.Confidence ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$hasOverlap", turn.HasOverlap ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
