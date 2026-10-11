using Application.Reportes.Eventos;
using MassTransit;

namespace UnitTests.Auditoria;

// El contrato está copiado de Reportes (plan D-003): en local, MassTransit enlaza la cola al exchange del tipo por su
// URN, que sale del namespace y el nombre. Si cambian, Auditoria deja de recibir los eventos de RabbitMQ.
public sealed class ContratoReportesTests
{
    [Fact]
    public void ReporteCreadoEvent_ConservaElUrnDeReportes()
    {
        Assert.Equal("urn:message:Application.Reportes.Eventos:ReporteCreadoEvent", MessageUrn.ForTypeString<ReporteCreadoEvent>());
    }
}
