// Copia del contrato publicado por urban-alert-reportes (mismo namespace: URN de MassTransit). Plan D-003.
namespace Domain.Reportes;

public enum EstadoReporte
{
    Reportado = 0,
    Verificado = 1,
    Asignado = 2,
    EnIntervencion = 3,
    Resuelto = 4,
    Rechazado = 5
}
