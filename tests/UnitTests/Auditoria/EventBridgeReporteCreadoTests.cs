using System.Text.Json;
using Application.Auditoria;
using Application.Reportes.Eventos;
using Domain.Reportes;

namespace UnitTests.Auditoria;

// Sobre de EventBridge tal como llega a la cola (destino sin transformador; plan §7.1).
public sealed class EventBridgeReporteCreadoTests
{
    private static readonly Guid IdEvento = Guid.Parse("0199d2a4-1111-7000-8000-000000000001");
    private static readonly Guid IdReporte = Guid.Parse("0199d2a4-2222-7000-8000-000000000002");
    private static readonly Guid IdCoordenada = Guid.Parse("0199d2a4-3333-7000-8000-000000000003");
    private static readonly Guid IdUsuario = Guid.Parse("0199d2a4-4444-7000-8000-000000000004");

    private static string Detalle(string estado) =>
        "{\"idEvento\":\"" + IdEvento + "\",\"reporte\":{\"id\":\"" + IdReporte + "\",\"estado\":" + estado +
        ",\"idCoordenada\":\"" + IdCoordenada + "\",\"idUsuario\":\"" + IdUsuario + "\",\"fecha\":\"2026-10-11T15:30:00Z\"}}";

    private static string Sobre(
        string estado = "\"REPORTADO\"",
        string source = "urban-alert.reportes",
        string detailType = "ReporteCreado",
        string? detail = null) =>
        "{\"version\":\"0\",\"id\":\"6a7e8feb-b491-4cf7-a9f1-bf3703467718\",\"detail-type\":\"" + detailType +
        "\",\"source\":\"" + source + "\",\"account\":\"348364742283\",\"time\":\"2026-10-11T15:30:01Z\"," +
        "\"region\":\"us-east-2\",\"resources\":[],\"detail\":" + (detail ?? Detalle(estado)) + "}";

    [Fact]
    public void Leer_ConvierteElDetalleEnElEventoDeReportes()
    {
        ReporteCreadoEvent evento = EventBridgeReporteCreado.Leer(Sobre());

        Assert.Equal(IdEvento, evento.IdEvento);
        Assert.Equal(IdReporte, evento.Reporte.Id);
        Assert.Equal(EstadoReporte.Reportado, evento.Reporte.Estado);
        Assert.Equal(IdCoordenada, evento.Reporte.IdCoordenada);
        Assert.Equal(IdUsuario, evento.Reporte.IdUsuario);
        Assert.Equal(new DateTime(2026, 10, 11, 15, 30, 0, DateTimeKind.Utc), evento.Reporte.Fecha.ToUniversalTime());
    }

    [Theory]
    [InlineData("\"EN_INTERVENCION\"", EstadoReporte.EnIntervencion)]
    [InlineData("3", EstadoReporte.EnIntervencion)]
    public void Leer_AceptaElEstadoComoCodigoOComoNumero(string estado, EstadoReporte esperado)
    {
        Assert.Equal(esperado, EventBridgeReporteCreado.Leer(Sobre(estado)).Reporte.Estado);
    }

    [Fact]
    public void Leer_ProduceElMismoRegistroQueElConsumidorDeMassTransit()
    {
        ReporteCreadoEvent esperado = new(IdEvento,
            new ReporteEventoDto(IdReporte, EstadoReporte.Reportado, IdCoordenada, IdUsuario,
                new DateTime(2026, 10, 11, 15, 30, 0, DateTimeKind.Utc)));

        AuditEvent desdeEventBridge = DotNetAuditEventFactory.FromReporteCreado(EventBridgeReporteCreado.Leer(Sobre()), null);
        AuditEvent desdeRabbit = DotNetAuditEventFactory.FromReporteCreado(esperado, null);

        Assert.Equal(desdeRabbit.EventId, desdeEventBridge.EventId);
        Assert.Equal(desdeRabbit.EventType, desdeEventBridge.EventType);
        Assert.Equal(desdeRabbit.OccurredAt, desdeEventBridge.OccurredAt);
        Assert.Null(desdeEventBridge.CorrelationId);
        Assert.Equal(desdeRabbit.Payload.GetRawText(), desdeEventBridge.Payload.GetRawText());
    }

    [Theory]
    [InlineData("urban-alert.otro", "ReporteCreado")]
    [InlineData("urban-alert.reportes", "ReporteEstadoActualizado")]
    public void Leer_RechazaOtroOrigenOTipo(string source, string detailType)
    {
        Assert.Throws<EventoInvalidoException>(() => EventBridgeReporteCreado.Leer(Sobre(source: source, detailType: detailType)));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"idEvento\":\"00000000-0000-0000-0000-000000000000\",\"reporte\":{\"id\":\"0199d2a4-2222-7000-8000-000000000002\",\"estado\":\"REPORTADO\",\"idCoordenada\":\"0199d2a4-3333-7000-8000-000000000003\",\"idUsuario\":\"0199d2a4-4444-7000-8000-000000000004\",\"fecha\":\"2026-10-11T15:30:00Z\"}}")]
    [InlineData("{\"idEvento\":\"0199d2a4-1111-7000-8000-000000000001\",\"reporte\":{\"id\":\"00000000-0000-0000-0000-000000000000\",\"estado\":\"REPORTADO\",\"idCoordenada\":\"0199d2a4-3333-7000-8000-000000000003\",\"idUsuario\":\"0199d2a4-4444-7000-8000-000000000004\",\"fecha\":\"2026-10-11T15:30:00Z\"}}")]
    [InlineData("{\"idEvento\":\"0199d2a4-1111-7000-8000-000000000001\",\"reporte\":{\"id\":\"0199d2a4-2222-7000-8000-000000000002\",\"estado\":\"NO_EXISTE\",\"idCoordenada\":\"0199d2a4-3333-7000-8000-000000000003\",\"idUsuario\":\"0199d2a4-4444-7000-8000-000000000004\",\"fecha\":\"2026-10-11T15:30:00Z\"}}")]
    public void Leer_RechazaUnDetalleIncompletoOInvalido(string detail)
    {
        Assert.Throws<EventoInvalidoException>(() => EventBridgeReporteCreado.Leer(Sobre(detail: detail)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no es json")]
    [InlineData("[]")]
    public void Leer_RechazaUnCuerpoQueNoEsUnSobre(string cuerpo)
    {
        Assert.Throws<EventoInvalidoException>(() => EventBridgeReporteCreado.Leer(cuerpo));
    }
}
