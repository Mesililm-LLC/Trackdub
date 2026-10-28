using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Transcript;
using DomainTranscriptSegment = Trackdub.Domain.Transcript.TranscriptSegment;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteTranscriptRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : ITranscriptRepository
{
    public async Task<TranscriptRevision?> GetCurrentRevisionAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   stage_run_id,
                   revision_number,
                   created_at_utc
            FROM transcript_revisions
            WHERE project_id = $projectId
            ORDER BY revision_number DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRevision(reader);
    }

    public async Task<IReadOnlyList<DomainTranscriptSegment>> GetSegmentsAsync(Guid transcriptRevisionId, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   transcript_revision_id,
                   speaker_id,
                   segment_index,
                   start_seconds,
                   end_seconds,
                   text,
                   detected_language,
                   speaker_assignment_origin
            FROM transcript_segments
            WHERE transcript_revision_id = $transcriptRevisionId
            ORDER BY segment_index;
            """;
        command.Parameters.AddWithValue("$transcriptRevisionId", transcriptRevisionId.ToString("D"));

        var results = new List<DomainTranscriptSegment>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DomainTranscriptSegment(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                reader.GetInt32(3),
                reader.GetDouble(4),
                reader.GetDouble(5),
                reader.GetString(6),
                reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                speakerAssignmentOrigin: Enum.IsDefined(typeof(SpeakerAssignmentOrigin), reader.GetInt32(8))
                    ? (SpeakerAssignmentOrigin)reader.GetInt32(8)
                    : SpeakerAssignmentOrigin.Unknown));
        }

        if (results.Count == 0)
        {
            return results;
        }

        IReadOnlyDictionary<Guid, IReadOnlyList<TranscriptWord>> wordsBySegmentId =
            await LoadWordsAsync(connection, results.Select(segment => segment.Id).ToArray(), cancellationToken)
                .ConfigureAwait(false);

        return results
            .Select(segment => segment with
            {
                Words = wordsBySegmentId.TryGetValue(segment.Id, out IReadOnlyList<TranscriptWord>? words)
                    ? words
                    : []
            })
            .ToArray();
    }

    public async Task<int> GetNextRevisionNumberAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(MAX(revision_number), 0)
            FROM transcript_revisions
            WHERE project_id = $projectId;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        int currentValue = Convert.ToInt32(result, System.Globalization.CultureInfo.InvariantCulture);
        return currentValue + 1;
    }

    public async Task SaveRevisionAsync(
        TranscriptRevision revision,
        IReadOnlyList<DomainTranscriptSegment> segments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(revision);
        ArgumentNullException.ThrowIfNull(segments);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand revisionCommand = connection.CreateCommand())
        {
            revisionCommand.Transaction = transaction;
            revisionCommand.CommandText =
                """
                INSERT INTO transcript_revisions (
                    id,
                    project_id,
                    stage_run_id,
                    revision_number,
                    created_at_utc)
                VALUES (
                    $id,
                    $projectId,
                    $stageRunId,
                    $revisionNumber,
                    $createdAtUtc);
                """;
            revisionCommand.Parameters.AddWithValue("$id", revision.Id.ToString("D"));
            revisionCommand.Parameters.AddWithValue("$projectId", revision.ProjectId.ToString("D"));
            revisionCommand.Parameters.AddWithValue("$stageRunId", revision.StageRunId?.ToString("D") ?? (object)DBNull.Value);
            revisionCommand.Parameters.AddWithValue("$revisionNumber", revision.RevisionNumber);
            revisionCommand.Parameters.AddWithValue("$createdAtUtc", revision.CreatedAtUtc.UtcDateTime);
            await revisionCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand segmentCommand = connection.CreateCommand();
        segmentCommand.Transaction = transaction;
        segmentCommand.CommandText =
            """
                INSERT INTO transcript_segments (
                    id,
                    transcript_revision_id,
                    speaker_id,
                    segment_index,
                    start_seconds,
                    end_seconds,
                    text,
                    detected_language,
                    speaker_assignment_origin)
                VALUES (
                    $id,
                    $transcriptRevisionId,
                    $speakerId,
                    $segmentIndex,
                    $startSeconds,
                    $endSeconds,
                    $text,
                    $detectedLanguage,
                    $speakerAssignmentOrigin);
            """;
        SqliteParameter segmentIdParameter = segmentCommand.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter transcriptRevisionIdParameter = segmentCommand.Parameters.Add("$transcriptRevisionId", SqliteType.Text);
        SqliteParameter speakerIdParameter = segmentCommand.Parameters.Add("$speakerId", SqliteType.Text);
        SqliteParameter segmentIndexParameter = segmentCommand.Parameters.Add("$segmentIndex", SqliteType.Integer);
        SqliteParameter startSecondsParameter = segmentCommand.Parameters.Add("$startSeconds", SqliteType.Real);
        SqliteParameter endSecondsParameter = segmentCommand.Parameters.Add("$endSeconds", SqliteType.Real);
        SqliteParameter textParameter = segmentCommand.Parameters.Add("$text", SqliteType.Text);
        SqliteParameter detectedLanguageParameter = segmentCommand.Parameters.Add("$detectedLanguage", SqliteType.Text);
        SqliteParameter speakerAssignmentOriginParameter = segmentCommand.Parameters.Add("$speakerAssignmentOrigin", SqliteType.Integer);

        await using SqliteCommand wordCommand = connection.CreateCommand();
        wordCommand.Transaction = transaction;
        wordCommand.CommandText =
            """
                INSERT INTO words (
                    id,
                    transcript_segment_id,
                    word_index,
                    start_seconds,
                    end_seconds,
                    text,
                    confidence)
                VALUES (
                    $id,
                    $transcriptSegmentId,
                    $wordIndex,
                    $startSeconds,
                    $endSeconds,
                    $text,
                    $confidence);
            """;
        SqliteParameter wordIdParameter = wordCommand.Parameters.Add("$id", SqliteType.Text);
        SqliteParameter wordSegmentIdParameter = wordCommand.Parameters.Add("$transcriptSegmentId", SqliteType.Text);
        SqliteParameter wordIndexParameter = wordCommand.Parameters.Add("$wordIndex", SqliteType.Integer);
        SqliteParameter wordStartSecondsParameter = wordCommand.Parameters.Add("$startSeconds", SqliteType.Real);
        SqliteParameter wordEndSecondsParameter = wordCommand.Parameters.Add("$endSeconds", SqliteType.Real);
        SqliteParameter wordTextParameter = wordCommand.Parameters.Add("$text", SqliteType.Text);
        SqliteParameter wordConfidenceParameter = wordCommand.Parameters.Add("$confidence", SqliteType.Real);

        foreach (DomainTranscriptSegment segment in segments.OrderBy(segment => segment.SegmentIndex))
        {
            segmentIdParameter.Value = segment.Id.ToString("D");
            transcriptRevisionIdParameter.Value = segment.TranscriptRevisionId.ToString("D");
            speakerIdParameter.Value = segment.SpeakerId?.ToString("D") ?? (object)DBNull.Value;
            segmentIndexParameter.Value = segment.SegmentIndex;
            startSecondsParameter.Value = segment.StartSeconds;
            endSecondsParameter.Value = segment.EndSeconds;
            textParameter.Value = segment.Text;
            detectedLanguageParameter.Value = segment.DetectedLanguage ?? (object)DBNull.Value;
            speakerAssignmentOriginParameter.Value = (int)segment.SpeakerAssignmentOrigin;
            await segmentCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            foreach (TranscriptWord word in segment.Words.OrderBy(static word => word.WordIndex))
            {
                wordIdParameter.Value = Guid.NewGuid().ToString("D");
                wordSegmentIdParameter.Value = segment.Id.ToString("D");
                wordIndexParameter.Value = word.WordIndex;
                wordStartSecondsParameter.Value = word.StartSeconds;
                wordEndSecondsParameter.Value = word.EndSeconds;
                wordTextParameter.Value = word.Text;
                wordConfidenceParameter.Value = word.Confidence ?? (object)DBNull.Value;
                await wordCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Commit take invalidation with the revision. A speaker, text or timing
        // change can no longer leave old audio marked current after interruption.
        await using (SqliteCommand staleTakes = connection.CreateCommand())
        {
            staleTakes.Transaction = transaction;
            staleTakes.CommandText =
                """
                UPDATE tts_takes
                SET is_stale = 1, status = 'Stale',
                    pre_stretch_duration_seconds = NULL, stretch_ratio_applied = NULL,
                    stretch_mode = 'None', stretch_engine = 'None'
                WHERE project_id = $projectId
                  AND $revisionId = (SELECT id FROM transcript_revisions
                                     WHERE project_id = $projectId ORDER BY revision_number DESC LIMIT 1)
                  AND EXISTS (
                      SELECT 1 FROM transcript_segments previous
                      WHERE previous.transcript_revision_id = (
                          SELECT id FROM transcript_revisions
                          WHERE project_id = $projectId AND id <> $revisionId
                          ORDER BY revision_number DESC LIMIT 1)
                        AND previous.segment_index = tts_takes.segment_index
                        AND NOT EXISTS (
                            SELECT 1 FROM transcript_segments current
                            WHERE current.transcript_revision_id = $revisionId
                              AND current.segment_index = previous.segment_index
                              AND current.speaker_id IS previous.speaker_id
                              AND current.start_seconds IS previous.start_seconds
                              AND current.end_seconds IS previous.end_seconds
                              AND current.text IS previous.text));
                """;
            staleTakes.Parameters.AddWithValue("$projectId", revision.ProjectId.ToString("D"));
            staleTakes.Parameters.AddWithValue("$revisionId", revision.Id.ToString("D"));
            await staleTakes.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReassignSpeakerAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await MarkSpeakerTakesStaleAsync(connection, transaction, projectId, sourceSpeakerId, cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE transcript_segments
            SET speaker_id = $targetSpeakerId, speaker_assignment_origin = 2
            WHERE speaker_id = $sourceSpeakerId
              AND transcript_revision_id IN (
                  SELECT id
                  FROM transcript_revisions
                  WHERE project_id = $projectId);
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$sourceSpeakerId", sourceSpeakerId.ToString("D"));
        command.Parameters.AddWithValue("$targetSpeakerId", targetSpeakerId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReassignAndMergeSpeakersAsync(
        Guid projectId,
        Guid sourceSpeakerId,
        Guid targetSpeakerId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        if (sourceSpeakerId == targetSpeakerId)
        {
            throw new InvalidOperationException("Source and target speaker must be different.");
        }

        await using (SqliteCommand validate = connection.CreateCommand())
        {
            validate.Transaction = transaction;
            validate.CommandText = "SELECT COUNT(*) FROM speakers WHERE project_id = $projectId AND merged_into_speaker_id IS NULL AND id IN ($source, $target);";
            validate.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
            validate.Parameters.AddWithValue("$source", sourceSpeakerId.ToString("D"));
            validate.Parameters.AddWithValue("$target", targetSpeakerId.ToString("D"));
            if (Convert.ToInt64(await validate.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture) != 2)
            {
                throw new InvalidOperationException("Both speakers must be active in this project.");
            }
        }

        await MarkSpeakerTakesStaleAsync(connection, transaction, projectId, sourceSpeakerId, cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand reassignSegments = connection.CreateCommand())
        {
            reassignSegments.Transaction = transaction;
            reassignSegments.CommandText =
                """
                UPDATE transcript_segments
                SET speaker_id = $targetSpeakerId, speaker_assignment_origin = 2
                WHERE speaker_id = $sourceSpeakerId
                  AND transcript_revision_id IN (
                      SELECT id
                      FROM transcript_revisions
                      WHERE project_id = $projectId);
                """;
            reassignSegments.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
            reassignSegments.Parameters.AddWithValue("$sourceSpeakerId", sourceSpeakerId.ToString("D"));
            reassignSegments.Parameters.AddWithValue("$targetSpeakerId", targetSpeakerId.ToString("D"));
            await reassignSegments.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand updateTurns = connection.CreateCommand())
        {
            updateTurns.Transaction = transaction;
            updateTurns.CommandText =
                """
                UPDATE speaker_turns
                SET speaker_id = $targetSpeakerId
                WHERE project_id = $projectId
                  AND speaker_id = $sourceSpeakerId;
                """;
            updateTurns.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
            updateTurns.Parameters.AddWithValue("$sourceSpeakerId", sourceSpeakerId.ToString("D"));
            updateTurns.Parameters.AddWithValue("$targetSpeakerId", targetSpeakerId.ToString("D"));
            await updateTurns.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (SqliteCommand archiveSpeaker = connection.CreateCommand())
        {
            archiveSpeaker.Transaction = transaction;
            archiveSpeaker.CommandText =
                """
                UPDATE speakers SET merged_into_speaker_id = $targetSpeakerId
                WHERE project_id = $projectId
                  AND id = $sourceSpeakerId;
                """;
            archiveSpeaker.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
            archiveSpeaker.Parameters.AddWithValue("$sourceSpeakerId", sourceSpeakerId.ToString("D"));
            archiveSpeaker.Parameters.AddWithValue("$targetSpeakerId", targetSpeakerId.ToString("D"));
            await archiveSpeaker.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task MarkSpeakerTakesStaleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid projectId,
        Guid sourceSpeakerId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE tts_takes
            SET is_stale = 1, status = 'Stale',
                pre_stretch_duration_seconds = NULL, stretch_ratio_applied = NULL,
                stretch_mode = 'None', stretch_engine = 'None'
            WHERE project_id = $projectId AND (
                voice_assignment_id IN (SELECT id FROM voice_assignments
                                        WHERE project_id = $projectId AND speaker_id = $sourceSpeakerId)
                OR segment_index IN (
                    SELECT segment_index FROM transcript_segments
                    WHERE speaker_id = $sourceSpeakerId AND transcript_revision_id = (
                        SELECT id FROM transcript_revisions WHERE project_id = $projectId
                        ORDER BY revision_number DESC LIMIT 1)));
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$sourceSpeakerId", sourceSpeakerId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static TranscriptRevision ReadRevision(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : Guid.Parse(reader.GetString(2)),
            reader.GetInt32(3),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc)));

    private static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<TranscriptWord>>> LoadWordsAsync(
        SqliteConnection connection,
        IReadOnlyList<Guid> segmentIds,
        CancellationToken cancellationToken)
    {
        if (segmentIds.Count == 0)
        {
            return new Dictionary<Guid, IReadOnlyList<TranscriptWord>>();
        }

        const string tempTableName = "temp_transcript_segment_filter";

        await using (SqliteCommand tempTableCommand = connection.CreateCommand())
        {
            tempTableCommand.CommandText =
                $"""
                CREATE TEMP TABLE IF NOT EXISTS {tempTableName} (
                    segment_id TEXT NOT NULL PRIMARY KEY
                );
                DELETE FROM {tempTableName};
                """;
            await tempTableCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await using (SqliteCommand insertCommand = connection.CreateCommand())
            {
                insertCommand.CommandText =
                    $"INSERT OR IGNORE INTO {tempTableName} (segment_id) VALUES ($segmentId);";
                SqliteParameter segmentIdParameter = insertCommand.Parameters.Add("$segmentId", SqliteType.Text);

                foreach (Guid segmentId in segmentIds)
                {
                    segmentIdParameter.Value = segmentId.ToString("D");
                    await insertCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            await using SqliteCommand queryCommand = connection.CreateCommand();
            queryCommand.CommandText =
                $"""
                 SELECT w.transcript_segment_id,
                        w.word_index,
                        w.start_seconds,
                        w.end_seconds,
                        w.text,
                        w.confidence
                 FROM words w
                 INNER JOIN {tempTableName} segments
                     ON segments.segment_id = w.transcript_segment_id
                 ORDER BY w.transcript_segment_id, w.word_index;
                 """;

            var wordsBySegmentId = new Dictionary<Guid, List<TranscriptWord>>();
            await using SqliteDataReader reader = await queryCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                Guid segmentId = Guid.Parse(reader.GetString(0));
                if (!wordsBySegmentId.TryGetValue(segmentId, out List<TranscriptWord>? words))
                {
                    words = [];
                    wordsBySegmentId[segmentId] = words;
                }

                words.Add(TranscriptWord.Create(
                    reader.GetInt32(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5)));
            }

            return wordsBySegmentId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TranscriptWord>)pair.Value
                    .OrderBy(static word => word.WordIndex)
                    .ToArray());
        }
        finally
        {
            await using SqliteCommand cleanupCommand = connection.CreateCommand();
            cleanupCommand.CommandText = $"DROP TABLE IF EXISTS {tempTableName};";
            await cleanupCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
