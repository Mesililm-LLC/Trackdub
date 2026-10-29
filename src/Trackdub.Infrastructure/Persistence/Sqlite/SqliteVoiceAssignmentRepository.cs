using Trackdub.Contracts;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain.Tts;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteVoiceAssignmentRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : IVoiceAssignmentRepository
{
    public async Task<VoiceAssignment?> GetByIdAsync(Guid projectId, Guid id, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease lease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = lease.Connection.CreateCommand();
        command.CommandText = """
            SELECT id, project_id, speaker_id, voice_model_id, voice_variant, requires_consent,
                   created_at_utc, is_fallback, reference_clip_artifact_id
            FROM voice_assignments WHERE project_id = $projectId AND id = $id;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadAssignment(reader) : null;
    }

    public async Task<VoiceAssignment?> GetAsync(
        Guid projectId,
        Guid speakerId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   speaker_id,
                   voice_model_id,
                   voice_variant,
                   requires_consent,
                   created_at_utc,
                   is_fallback,
                   reference_clip_artifact_id
            FROM voice_assignments
            WHERE project_id = $projectId
              AND speaker_id = $speakerId
              AND EXISTS (SELECT 1 FROM speakers WHERE id = $speakerId AND merged_into_speaker_id IS NULL)
              AND is_fallback = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$speakerId", speakerId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAssignment(reader)
            : null;
    }

    public async Task<VoiceAssignment?> GetForSpeakerIncludingMergedAsync(
        Guid projectId,
        Guid speakerId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   speaker_id,
                   voice_model_id,
                   voice_variant,
                   requires_consent,
                   created_at_utc,
                   is_fallback,
                   reference_clip_artifact_id
            FROM voice_assignments
            WHERE project_id = $projectId
              AND speaker_id = $speakerId
              AND is_fallback = 0
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));
        command.Parameters.AddWithValue("$speakerId", speakerId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadAssignment(reader)
            : null;
    }

    public async Task<IReadOnlyList<VoiceAssignment>> GetAllAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id,
                   project_id,
                   speaker_id,
                   voice_model_id,
                   voice_variant,
                   requires_consent,
                   created_at_utc,
                   is_fallback,
                   reference_clip_artifact_id
            FROM voice_assignments
            WHERE project_id = $projectId
              AND is_fallback = 0
              AND EXISTS (SELECT 1 FROM speakers WHERE speakers.id = voice_assignments.speaker_id AND speakers.merged_into_speaker_id IS NULL)
            ORDER BY created_at_utc;
            """;
        command.Parameters.AddWithValue("$projectId", projectId.ToString("D"));

        var results = new List<VoiceAssignment>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadAssignment(reader));
        }

        return results;
    }

    public async Task SaveAsync(VoiceAssignment assignment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO voice_assignments (
                id,
                project_id,
                speaker_id,
                voice_model_id,
                voice_variant,
                requires_consent,
                is_fallback,
                reference_clip_artifact_id,
                created_at_utc)
            VALUES (
                $id,
                $projectId,
                $speakerId,
                $voiceModelId,
                $voiceVariant,
                $requiresConsent,
                $isFallback,
                $referenceClipArtifactId,
                $createdAtUtc)
            ON CONFLICT(project_id, speaker_id) WHERE is_fallback = 0 DO UPDATE SET
                voice_model_id = excluded.voice_model_id,
                voice_variant = excluded.voice_variant,
                requires_consent = excluded.requires_consent,
                is_fallback = excluded.is_fallback,
                reference_clip_artifact_id = excluded.reference_clip_artifact_id;
            """;
        command.Parameters.AddWithValue("$id", assignment.Id.ToString("D"));
        command.Parameters.AddWithValue("$projectId", assignment.ProjectId.ToString("D"));
        command.Parameters.AddWithValue("$speakerId", assignment.SpeakerId.ToString("D"));
        command.Parameters.AddWithValue("$voiceModelId", assignment.VoiceModelId);
        command.Parameters.AddWithValue("$voiceVariant", assignment.VoiceVariant ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$requiresConsent", assignment.RequiresConsent ? 1 : 0);
        command.Parameters.AddWithValue("$isFallback", assignment.IsFallback ? 1 : 0);
        command.Parameters.AddWithValue("$referenceClipArtifactId", assignment.ReferenceClipArtifactId?.ToString("D") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", assignment.CreatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM voice_assignments WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static VoiceAssignment ReadAssignment(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5) == 1,
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(6), DateTimeKind.Utc)),
            reader.GetInt64(7) == 1,
            reader.IsDBNull(8) ? null : Guid.Parse(reader.GetString(8)));

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
