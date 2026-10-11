using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Application.Auditoria;
using Auditoria.Lambda;
using Infrastructure.Archivado;
using Infrastructure.Persistencia;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace IntegrationTests.Auditoria;

// Spec RF-004 y MS-002: registro idempotente y cadena íntegra con entregas repetidas y simultáneas
// (el mapeo de la cola permite hasta 2 invocaciones a la vez).
[Collection(AuditDatabaseCollection.Name)]
public sealed class RegistroIntegracionTests(AuditDatabaseFixture database)
{
    private readonly AuditStore _store = new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AuditWriter"] = database.ConnectionString,
            ["ConnectionStrings:AuditReader"] = database.ConnectionString
        })
        .Build());

    private RegistroHandler Handler() => new(_store, new NoOpAuditArchive(), NullLogger<RegistroHandler>.Instance);

    private static SQSEvent.SQSMessage Mensaje(Guid idEvento, Guid idReporte) => new()
    {
        MessageId = Guid.NewGuid().ToString(),
        Body = "{\"version\":\"0\",\"id\":\"" + Guid.NewGuid() + "\",\"detail-type\":\"ReporteCreado\",\"source\":\"urban-alert.reportes\"," +
               "\"time\":\"2026-10-11T15:30:01Z\",\"detail\":{\"idEvento\":\"" + idEvento + "\",\"reporte\":{\"id\":\"" + idReporte +
               "\",\"estado\":\"REPORTADO\",\"idCoordenada\":\"" + Guid.NewGuid() + "\",\"idUsuario\":\"" + Guid.NewGuid() +
               "\",\"fecha\":\"2026-10-11T15:30:00Z\"}}}"
    };

    [Fact]
    public async Task MismoMensajeDosVeces_RegistraUnSoloEvento()
    {
        Guid idReporte = Guid.NewGuid();
        SQSEvent.SQSMessage mensaje = Mensaje(Guid.NewGuid(), idReporte);

        SQSBatchResponse primera = await Handler().ManejarAsync(new SQSEvent { Records = [mensaje] }, new TestLambdaContext());
        SQSBatchResponse repetida = await Handler().ManejarAsync(new SQSEvent { Records = [mensaje] }, new TestLambdaContext());

        Assert.Empty(primera.BatchItemFailures);
        Assert.Empty(repetida.BatchItemFailures);
        AuditTimeline historial = await _store.GetTimelineAsync(idReporte, 0, 10, CancellationToken.None);
        Assert.Single(historial.Items);
    }

    [Fact]
    public async Task VeinteEventosEnDosInvocacionesParalelas_DejanLaCadenaIntegra()
    {
        Guid[] reportes = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        SQSEvent Lote(IEnumerable<Guid> ids) => new() { Records = ids.Select(id => Mensaje(Guid.NewGuid(), id)).ToList() };

        SQSBatchResponse[] respuestas = await Task.WhenAll(
            Handler().ManejarAsync(Lote(reportes.Take(10)), new TestLambdaContext()),
            Handler().ManejarAsync(Lote(reportes.Skip(10)), new TestLambdaContext()));

        Assert.All(respuestas, r => Assert.Empty(r.BatchItemFailures));
        foreach (Guid idReporte in reportes)
        {
            AuditIntegrityReport integridad = await _store.VerifyAsync(idReporte, CancellationToken.None);
            Assert.True(integridad.Verified, $"Cadena rota para el reporte {idReporte}");
            Assert.Equal(1, integridad.ReportEventCount);
        }
    }
}
