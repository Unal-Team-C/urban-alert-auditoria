using System.Text.Json;
using Application.Comun;
using Application.Reportes.Eventos;

namespace Application.Auditoria;

public sealed class EventoInvalidoException(string message, Exception? innerException = null)
    : Exception(message, innerException);

// En AWS, Reportes publica ReporteCreadoEvent en el bus de EventBridge y la regla de Auditoria entrega el sobre completo
// a la cola (sin transformador). El detalle es el mismo contrato que llega por RabbitMQ en local (spec RF-003).
public static class EventBridgeReporteCreado
{
    public const string Source = "urban-alert.reportes";
    public const string DetailType = "ReporteCreado";

    private static readonly JsonSerializerOptions Opciones =
        CodigosEnum.ConfigurarMensajeria(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static ReporteCreadoEvent Leer(string sobre)
    {
        JsonDocument documento;
        try
        {
            documento = JsonDocument.Parse(sobre);
        }
        catch (JsonException exception)
        {
            throw new EventoInvalidoException("El mensaje no es un evento de EventBridge en JSON.", exception);
        }

        using (documento)
        {
            JsonElement raiz = documento.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
                throw new EventoInvalidoException("El mensaje no es un evento de EventBridge en JSON.");

            string? source = Texto(raiz, "source");
            string? detailType = Texto(raiz, "detail-type");
            if (source != Source || detailType != DetailType)
                throw new EventoInvalidoException($"Evento no auditado: source '{source}', detail-type '{detailType}'.");

            if (!raiz.TryGetProperty("detail", out JsonElement detalle) || detalle.ValueKind != JsonValueKind.Object)
                throw new EventoInvalidoException("El evento no trae el detalle de ReporteCreado.");

            ReporteCreadoEvent? evento;
            try
            {
                evento = detalle.Deserialize<ReporteCreadoEvent>(Opciones);
            }
            catch (JsonException exception)
            {
                throw new EventoInvalidoException("El detalle de ReporteCreado no tiene el formato esperado.", exception);
            }

            Validar(evento);
            return evento!;
        }
    }

    private static string? Texto(JsonElement raiz, string propiedad) =>
        raiz.TryGetProperty(propiedad, out JsonElement valor) && valor.ValueKind == JsonValueKind.String ? valor.GetString() : null;

    private static void Validar(ReporteCreadoEvent? evento)
    {
        if (evento is null || evento.IdEvento == Guid.Empty)
            throw new EventoInvalidoException("Falta idEvento en el detalle de ReporteCreado.");
        if (evento.Reporte is null || evento.Reporte.Id == Guid.Empty)
            throw new EventoInvalidoException("Falta reporte.id en el detalle de ReporteCreado.");
        if (evento.Reporte.IdUsuario == Guid.Empty || evento.Reporte.IdCoordenada == Guid.Empty)
            throw new EventoInvalidoException("Faltan reporte.idUsuario o reporte.idCoordenada en el detalle de ReporteCreado.");
        if (!Enum.IsDefined(evento.Reporte.Estado))
            throw new EventoInvalidoException("reporte.estado no es un estado conocido.");
        if (evento.Reporte.Fecha == default)
            throw new EventoInvalidoException("Falta reporte.fecha en el detalle de ReporteCreado.");
    }
}
