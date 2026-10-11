using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Auditoria.Lambda;
using Microsoft.Extensions.Logging;
using Moq;

namespace UnitTests.Lambda;

public sealed class RegistroHandlerTests
{
    private static string Sobre(Guid idEvento, Guid idReporte) =>
        "{\"version\":\"0\",\"id\":\"" + Guid.NewGuid() + "\",\"detail-type\":\"ReporteCreado\",\"source\":\"urban-alert.reportes\"," +
        "\"time\":\"2026-10-11T15:30:01Z\",\"detail\":{\"idEvento\":\"" + idEvento + "\",\"reporte\":{\"id\":\"" + idReporte +
        "\",\"estado\":\"REPORTADO\",\"idCoordenada\":\"" + Guid.NewGuid() + "\",\"idUsuario\":\"" + Guid.NewGuid() +
        "\",\"fecha\":\"2026-10-11T15:30:00Z\"}}}";

    private static SQSEvent.SQSMessage Mensaje(string id, string cuerpo) => new() { MessageId = id, Body = cuerpo };

    private static AuditEventResponse Respuesta(AuditEvent evento) => new(1, evento.EventId, evento.EventType, evento.Version,
        evento.OccurredAt, evento.ActorId, evento.CorrelationId, evento.Data, new string('0', 64), new string('a', 64), evento.Payload);

    [Fact]
    public async Task ManejarAsync_DevuelveSoloLosMensajesFallidosYPersisteElResto()
    {
        Guid valido1 = Guid.NewGuid(), valido2 = Guid.NewGuid(), falla = Guid.NewGuid();
        Mock<IAuditStore> store = new();
        store.Setup(s => s.PersistAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEvent e, CancellationToken _) => e.EventId == falla
                ? throw new InvalidOperationException("base no disponible")
                : Respuesta(e));
        Mock<IAuditArchive> archive = new();
        RegistroHandler handler = new(store.Object, archive.Object, new LoggerQueCaptura<RegistroHandler>());
        SQSEvent lote = new()
        {
            Records =
            [
                Mensaje("m-1", Sobre(valido1, Guid.NewGuid())),
                Mensaje("m-2", "{\"source\":\"urban-alert.reportes\",\"detail-type\":\"ReporteCreado\",\"detail\":{}}"),
                Mensaje("m-3", Sobre(falla, Guid.NewGuid())),
                Mensaje("m-4", Sobre(valido2, Guid.NewGuid()))
            ]
        };

        SQSBatchResponse respuesta = await handler.ManejarAsync(lote, new TestLambdaContext());

        Assert.Equal(["m-2", "m-3"], respuesta.BatchItemFailures.Select(f => f.ItemIdentifier).Order().ToArray());
        store.Verify(s => s.PersistAsync(It.Is<AuditEvent>(e => e.EventId == valido1), It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.PersistAsync(It.Is<AuditEvent>(e => e.EventId == valido2), It.IsAny<CancellationToken>()), Times.Once);
        archive.Verify(a => a.ArchiveAsync(It.IsAny<AuditEventResponse>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task ManejarAsync_LoteSinErroresNoDevuelveFallos()
    {
        Mock<IAuditStore> store = new();
        store.Setup(s => s.PersistAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEvent e, CancellationToken _) => Respuesta(e));
        RegistroHandler handler = new(store.Object, Mock.Of<IAuditArchive>(), new LoggerQueCaptura<RegistroHandler>());

        SQSBatchResponse respuesta = await handler.ManejarAsync(
            new SQSEvent { Records = [Mensaje("m-1", Sobre(Guid.NewGuid(), Guid.NewGuid()))] }, new TestLambdaContext());

        Assert.Empty(respuesta.BatchItemFailures);
    }

    [Fact]
    public async Task ManejarAsync_RegistraUnLogPorEventoConSusIdentificadores()
    {
        Guid idEvento = Guid.NewGuid(), idReporte = Guid.NewGuid();
        Mock<IAuditStore> store = new();
        store.Setup(s => s.PersistAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEvent e, CancellationToken _) => Respuesta(e));
        LoggerQueCaptura<RegistroHandler> logger = new();
        RegistroHandler handler = new(store.Object, Mock.Of<IAuditArchive>(), logger);

        await handler.ManejarAsync(new SQSEvent { Records = [Mensaje("m-9", Sobre(idEvento, idReporte))] }, new TestLambdaContext());

        string linea = Assert.Single(logger.Lineas, l => l.Nivel == LogLevel.Information).Texto;
        Assert.Contains(idEvento.ToString(), linea);
        Assert.Contains(idReporte.ToString(), linea);
        Assert.Contains("m-9", linea);
    }

    [Fact]
    public async Task ManejarAsync_UnMensajeInvalidoDejaUnErrorConSuIdentificador()
    {
        LoggerQueCaptura<RegistroHandler> logger = new();
        RegistroHandler handler = new(Mock.Of<IAuditStore>(), Mock.Of<IAuditArchive>(), logger);

        SQSBatchResponse respuesta = await handler.ManejarAsync(
            new SQSEvent { Records = [Mensaje("m-x", "no es json")] }, new TestLambdaContext());

        Assert.Equal("m-x", Assert.Single(respuesta.BatchItemFailures).ItemIdentifier);
        Assert.Contains(logger.Lineas, l => l.Nivel == LogLevel.Error && l.Texto.Contains("m-x"));
    }
}

internal sealed class LoggerQueCaptura<T> : ILogger<T>
{
    public List<(LogLevel Nivel, string Texto)> Lineas { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Lineas.Add((logLevel, formatter(state, exception)));
}
