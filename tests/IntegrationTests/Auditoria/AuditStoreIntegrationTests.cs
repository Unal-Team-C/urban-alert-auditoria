using Application.Auditoria;
using Application.Reportes.Eventos;
using Infrastructure.Persistencia;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace IntegrationTests.Auditoria;

[Collection(AuditDatabaseCollection.Name)]
public sealed class AuditStoreIntegrationTests
{
    private readonly AuditDatabaseFixture _database;
    private readonly AuditStore _store;

    public AuditStoreIntegrationTests(AuditDatabaseFixture database)
    {
        _database = database;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:AuditWriter"] = database.ConnectionString,
                ["ConnectionStrings:AuditReader"] = database.ConnectionString
            })
            .Build();
        _store = new AuditStore(configuration);
    }

    [Fact]
    public async Task PersistAsync_IsIdempotentForTheSameEventId()
    {
        AuditEvent auditEvent = CreateEvent(Guid.NewGuid(), Guid.NewGuid());
        long initialCount = await _database.CountEventsAsync();

        AuditEventResponse first = await _store.PersistAsync(auditEvent, CancellationToken.None);
        AuditEventResponse replay = await _store.PersistAsync(auditEvent, CancellationToken.None);

        Assert.Equal(first.Sequence, replay.Sequence);
        Assert.Equal(first.Hash, replay.Hash);
        Assert.Equal(initialCount + 1, await _database.CountEventsAsync());
    }

    [Fact]
    public async Task Timeline_IsPaginatedAndIntegrityIncludesTheGlobalChain()
    {
        Guid reportId = Guid.NewGuid();
        AuditEvent firstEvent = CreateEvent(Guid.NewGuid(), reportId);
        AuditEvent secondEvent = CreateEvent(Guid.NewGuid(), reportId);

        AuditEventResponse first = await _store.PersistAsync(firstEvent, CancellationToken.None);
        AuditEventResponse second = await _store.PersistAsync(secondEvent, CancellationToken.None);
        AuditTimeline firstPage = await _store.GetTimelineAsync(reportId, 0, 1, CancellationToken.None);
        AuditTimeline secondPage = await _store.GetTimelineAsync(reportId, first.Sequence, 1, CancellationToken.None);
        AuditIntegrityReport integrity = await _store.VerifyAsync(reportId, CancellationToken.None);

        Assert.Single(firstPage.Items);
        Assert.Equal(first.EventId, firstPage.Items[0].EventId);
        Assert.Equal(first.Sequence.ToString(), firstPage.NextCursor);
        Assert.Single(secondPage.Items);
        Assert.Equal(second.EventId, secondPage.Items[0].EventId);
        Assert.Null(secondPage.NextCursor);
        Assert.True(integrity.Verified);
        Assert.Equal(2, integrity.ReportEventCount);
        Assert.Equal(checked((int)await _database.CountEventsAsync()), integrity.ChainEventCount);
    }

    [Fact]
    public async Task AuditEvents_RejectUpdateAndDeleteAtDatabaseLevel()
    {
        AuditEvent auditEvent = CreateEvent(Guid.NewGuid(), Guid.NewGuid());
        await _store.PersistAsync(auditEvent, CancellationToken.None);

        await using NpgsqlConnection connection = new(_database.ConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand update = new(
            "UPDATE audit_events SET event_type = 'tampered' WHERE event_id = @eventId", connection);
        update.Parameters.AddWithValue("eventId", auditEvent.EventId);

        await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
    }

    private static AuditEvent CreateEvent(Guid eventId, Guid reportId)
    {
        Guid actorId = Guid.NewGuid();
        ReporteCreadoEvent message = new(eventId,
            new ReporteEventoDto(reportId, Domain.Reportes.EstadoReporte.Reportado, Guid.NewGuid(), actorId,
                new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc)));
        return DotNetAuditEventFactory.FromReporteCreado(message, correlationId: null);
    }
}
