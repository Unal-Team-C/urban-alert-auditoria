using System.Globalization;
using System.Text.Json;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Domain.Auditoria;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Persistencia;

public sealed class AuditStore(IConfiguration configuration) : IAuditStore
{
    private string WriterConnectionString => RequiredConnectionString("AuditWriter");
    private string ReaderConnectionString => RequiredConnectionString("AuditReader");

    public async Task<AuditEventResponse> PersistAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = new(WriterConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (NpgsqlCommand lockCommand = new(
            "SELECT last_hash FROM audit_chain_state WHERE singleton = TRUE FOR UPDATE", connection, transaction))
        {
            object? state = await lockCommand.ExecuteScalarAsync(cancellationToken);
            if (state is null or DBNull)
                throw new InvalidOperationException("No existe el estado inicial de la cadena de auditoría.");

            await using NpgsqlCommand existingCommand = new(
                "SELECT sequence_id, payload::text, previous_hash, record_hash FROM audit_events WHERE event_id = @eventId",
                connection, transaction);
            existingCommand.Parameters.AddWithValue("eventId", auditEvent.EventId);
            await using NpgsqlDataReader existingReader = await existingCommand.ExecuteReaderAsync(cancellationToken);
            if (await existingReader.ReadAsync(cancellationToken))
            {
                long sequence = existingReader.GetInt64(0);
                string payload = existingReader.GetString(1);
                string previous = existingReader.GetString(2).Trim();
                string hash = existingReader.GetString(3).Trim();
                await existingReader.DisposeAsync();
                await transaction.CommitAsync(cancellationToken);
                return ToResponse(sequence, payload, previous, hash);
            }

            string previousHash = Convert.ToString(state, CultureInfo.InvariantCulture)!.Trim();
            string recordHash = AuditHash.Compute(auditEvent.Payload, previousHash);
            await existingReader.DisposeAsync();

            await using NpgsqlCommand insertCommand = new(
                """
                INSERT INTO audit_events
                    (event_id, event_type, event_version, occurred_at, correlation_id,
                     report_id, actor_id, payload, previous_hash, record_hash)
                VALUES
                    (@eventId, @eventType, @version, @occurredAt, @correlationId,
                     @reportId, @actorId, @payload, @previousHash, @recordHash)
                RETURNING sequence_id
                """, connection, transaction);
            insertCommand.Parameters.AddWithValue("eventId", auditEvent.EventId);
            insertCommand.Parameters.AddWithValue("eventType", auditEvent.EventType);
            insertCommand.Parameters.AddWithValue("version", auditEvent.Version);
            insertCommand.Parameters.AddWithValue("occurredAt", auditEvent.OccurredAt);
            insertCommand.Parameters.Add(new NpgsqlParameter("correlationId", NpgsqlDbType.Uuid)
            {
                Value = (object?)auditEvent.CorrelationId ?? DBNull.Value
            });
            insertCommand.Parameters.Add(new NpgsqlParameter("reportId", NpgsqlDbType.Uuid)
            {
                Value = (object?)auditEvent.ReportId ?? DBNull.Value
            });
            insertCommand.Parameters.AddWithValue("actorId", auditEvent.ActorId);
            insertCommand.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb)
            {
                Value = auditEvent.Payload.GetRawText()
            });
            insertCommand.Parameters.AddWithValue("previousHash", previousHash);
            insertCommand.Parameters.AddWithValue("recordHash", recordHash);
            long newSequence = Convert.ToInt64(await insertCommand.ExecuteScalarAsync(cancellationToken),
                CultureInfo.InvariantCulture);

            await using NpgsqlCommand updateCommand = new(
                "UPDATE audit_chain_state SET last_hash = @hash, last_event_id = @eventId, updated_at = now() WHERE singleton = TRUE",
                connection, transaction);
            updateCommand.Parameters.AddWithValue("hash", recordHash);
            updateCommand.Parameters.AddWithValue("eventId", auditEvent.EventId);
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return ToResponse(newSequence, auditEvent.Payload.GetRawText(), previousHash, recordHash);
        }
    }

    public async Task<AuditTimeline> GetTimelineAsync(Guid reportId, long cursor, int limit, CancellationToken cancellationToken)
    {
        List<AuditEventResponse> records = [];
        await using NpgsqlConnection connection = new(ReaderConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            """
            SELECT sequence_id, payload::text, previous_hash, record_hash
            FROM audit_events
            WHERE report_id = @reportId AND sequence_id > @cursor
            ORDER BY sequence_id ASC
            LIMIT @take
            """, connection);
        command.Parameters.AddWithValue("reportId", reportId);
        command.Parameters.AddWithValue("cursor", cursor);
        command.Parameters.AddWithValue("take", limit + 1);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            records.Add(ToResponse(reader.GetInt64(0), reader.GetString(1), reader.GetString(2).Trim(), reader.GetString(3).Trim()));

        string? nextCursor = records.Count > limit ? records[limit - 1].Sequence.ToString(CultureInfo.InvariantCulture) : null;
        if (records.Count > limit)
            records.RemoveAt(records.Count - 1);
        return new AuditTimeline(reportId, records, nextCursor);
    }

    public async Task<AuditIntegrityReport> VerifyAsync(Guid reportId, CancellationToken cancellationToken)
    {
        List<AuditChainEntry> entries = [];
        string? chainHead;
        await using (NpgsqlConnection connection = new(ReaderConnectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using NpgsqlCommand eventsCommand = new(
                "SELECT sequence_id, report_id, payload::text, previous_hash, record_hash FROM audit_events ORDER BY sequence_id ASC",
                connection);
            await using NpgsqlDataReader reader = await eventsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                Guid? storedReportId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
                using JsonDocument payload = JsonDocument.Parse(reader.GetString(2));
                entries.Add(new AuditChainEntry(reader.GetInt64(0), storedReportId, payload.RootElement.Clone(),
                    reader.GetString(3).Trim(), reader.GetString(4).Trim()));
            }
            await reader.DisposeAsync();

            await using NpgsqlCommand stateCommand = new(
                "SELECT last_hash FROM audit_chain_state WHERE singleton = TRUE", connection);
            object? storedHead = await stateCommand.ExecuteScalarAsync(cancellationToken);
            chainHead = storedHead is null or DBNull ? null : Convert.ToString(storedHead, CultureInfo.InvariantCulture)?.Trim();
        }

        AuditIntegrityResult result = AuditIntegrityVerifier.Verify(entries, chainHead, reportId);
        return new AuditIntegrityReport(reportId, result.Verified, result.ReportEventCount,
            result.ChainEventCount, result.HeadMatches, result.BrokenSequence, DateTimeOffset.UtcNow);
    }

    private string RequiredConnectionString(string name) =>
        configuration.GetConnectionString(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Falta configurar ConnectionStrings:{name}.");

    private static AuditEventResponse ToResponse(long sequence, string payloadJson, string previousHash, string hash)
    {
        using JsonDocument payload = JsonDocument.Parse(payloadJson);
        JsonElement root = payload.RootElement;
        JsonElement correlationId = root.GetProperty("correlationId");
        return new AuditEventResponse(sequence,
            Guid.Parse(root.GetProperty("eventId").GetString()!),
            root.GetProperty("eventType").GetString()!,
            root.GetProperty("version").GetInt32(),
            root.GetProperty("occurredAt").GetDateTimeOffset(),
            Guid.Parse(root.GetProperty("data").GetProperty("actorId").GetString()!),
            correlationId.ValueKind == JsonValueKind.Null ? null : Guid.Parse(correlationId.GetString()!),
            root.GetProperty("data").Clone(), previousHash.Trim(), hash.Trim(), root.Clone());
    }
}
