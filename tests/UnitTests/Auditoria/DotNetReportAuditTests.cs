using System.Text.Json;
using Application.Auditoria;
using Application.Reportes.Eventos;
using Domain.Auditoria;
using Domain.Reportes;

namespace UnitTests.Auditoria;

public sealed class DotNetReportAuditTests
{
    [Fact]
    public void Factory_MapsCurrentMassTransitReportCreatedContract()
    {
        Guid eventId = Guid.NewGuid();
        Guid reportId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        Guid coordinateId = Guid.NewGuid();
        DateTime createdAt = new(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc);
        ReporteCreadoEvent message = new(eventId,
            new ReporteEventoDto(reportId, EstadoReporte.Reportado, coordinateId, actorId, createdAt));

        AuditEvent auditEvent = DotNetAuditEventFactory.FromReporteCreado(message, correlationId: null);

        Assert.Equal(eventId, auditEvent.EventId);
        Assert.Equal("reporte.creado", auditEvent.EventType);
        Assert.Equal(reportId, auditEvent.ReportId);
        Assert.Equal(actorId, auditEvent.ActorId);
        Assert.Null(auditEvent.CorrelationId);
        Assert.Equal(coordinateId, auditEvent.Data.GetProperty("idCoordenada").GetGuid());
        Assert.Equal("REPORTADO", auditEvent.Data.GetProperty("estado").GetString());
        Assert.Equal(JsonValueKind.Null, auditEvent.Payload.GetProperty("correlationId").ValueKind);
    }

    [Fact]
    public void IntegrityVerifier_CoversInterleavedDotNetReports()
    {
        AuditEvent first = CreateAuditEvent(Guid.NewGuid(), Guid.NewGuid());
        AuditEvent second = CreateAuditEvent(Guid.NewGuid(), Guid.NewGuid());
        string firstHash = AuditHash.Compute(first.Payload, AuditHash.GenesisHash);
        string secondHash = AuditHash.Compute(second.Payload, firstHash);
        List<AuditChainEntry> entries =
        [
            new(1, first.ReportId, first.Payload, AuditHash.GenesisHash, firstHash),
            new(2, second.ReportId, second.Payload, firstHash, secondHash)
        ];

        AuditIntegrityResult result = AuditIntegrityVerifier.Verify(entries, secondHash, first.ReportId!.Value);

        Assert.True(result.Verified);
        Assert.Equal(1, result.ReportEventCount);
        Assert.Equal(2, result.ChainEventCount);
    }

    [Fact]
    public void IntegrityVerifier_ReportsTamperedPayload()
    {
        AuditEvent auditEvent = CreateAuditEvent(Guid.NewGuid(), Guid.NewGuid());
        AuditChainEntry entry = new(1, auditEvent.ReportId, auditEvent.Payload,
            AuditHash.GenesisHash, new string('f', 64));

        AuditIntegrityResult result = AuditIntegrityVerifier.Verify([entry], entry.Hash, auditEvent.ReportId!.Value);

        Assert.False(result.Verified);
        Assert.Equal(1L, result.BrokenSequence);
        Assert.False(result.HeadMatches);
    }

    private static AuditEvent CreateAuditEvent(Guid eventId, Guid reportId)
    {
        Guid actorId = Guid.NewGuid();
        ReporteCreadoEvent message = new(eventId,
            new ReporteEventoDto(reportId, EstadoReporte.Reportado, Guid.NewGuid(), actorId,
                new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc)));
        return DotNetAuditEventFactory.FromReporteCreado(message, correlationId: null);
    }
}
