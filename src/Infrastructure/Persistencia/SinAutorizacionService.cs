using System.Security.Claims;
using Application.Auditoria;
using Application.Interfaces.Auditoria;

namespace Infrastructure.Persistencia;

// Autenticacion:Modo=Ninguna (plan D-006): sin Cognito ni lectura de la base de Reportes; cualquier petición
// puede consultar el historial y la integridad de cualquier reporte (decisión del usuario, spec RF-007).
public sealed class SinAutorizacionService : IAuditAuthorizationService
{
    public Task<AuditUserContext> ResolveUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken) =>
        Task.FromResult(new AuditUserContext(Guid.Empty, "admin"));

    public Task<int?> CheckReportAccessAsync(AuditUserContext user, Guid reportId, CancellationToken cancellationToken) =>
        Task.FromResult<int?>(null);
}
