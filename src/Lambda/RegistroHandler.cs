using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Auditoria.Lambda;

// Función urban-alert-auditoria-registro: recibe lotes de la cola urban-alert-auditoria-eventos, que es destino de la
// regla de Auditoria en el bus de EventBridge (spec RF-001, RF-003). Procesa el lote en serie y devuelve solo los
// mensajes fallidos (ReportBatchItemFailures): se reintentan y tras 5 intentos pasan a la cola de fallidos (RF-005).
// La idempotencia por idEvento la garantiza AuditStore (RF-004).
public sealed class RegistroHandler(IAuditStore auditStore, IAuditArchive auditArchive, ILogger<RegistroHandler> logger)
{
    public RegistroHandler()
        : this(
            Arranque.Servicios.GetRequiredService<IAuditStore>(),
            Arranque.Servicios.GetRequiredService<IAuditArchive>(),
            Arranque.Servicios.GetRequiredService<ILogger<RegistroHandler>>())
    {
    }

    public async Task<SQSBatchResponse> ManejarAsync(SQSEvent evento, ILambdaContext contexto)
    {
        List<SQSBatchResponse.BatchItemFailure> fallidos = [];
        foreach (SQSEvent.SQSMessage mensaje in evento.Records ?? [])
        {
            try
            {
                AuditEvent auditEvent = DotNetAuditEventFactory.FromReporteCreado(EventBridgeReporteCreado.Leer(mensaje.Body), null);
                AuditEventResponse registrado = await auditStore.PersistAsync(auditEvent, CancellationToken.None);
                await auditArchive.ArchiveAsync(registrado, CancellationToken.None);
                logger.LogInformation(
                    "Evento auditado. MessageId={MessageId} EventId={EventId} ReportId={ReportId} Sequence={Sequence}",
                    mensaje.MessageId, auditEvent.EventId, auditEvent.ReportId, registrado.Sequence);
            }
            catch (EventoInvalidoException exception)
            {
                logger.LogError("Mensaje no auditado: {Motivo} MessageId={MessageId}", exception.Message, mensaje.MessageId);
                fallidos.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = mensaje.MessageId });
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Error al auditar el mensaje. MessageId={MessageId}", mensaje.MessageId);
                fallidos.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = mensaje.MessageId });
            }
        }

        return new SQSBatchResponse(fallidos);
    }
}
