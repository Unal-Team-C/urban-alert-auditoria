using System.Security.Claims;
using Application.Auditoria;

namespace Application.Interfaces.Auditoria;

public sealed class AuditAuthorizationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public interface IAuditAuthorizationService
{
    Task<AuditUserContext> ResolveUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<int?> CheckReportAccessAsync(AuditUserContext user, Guid reportId, CancellationToken cancellationToken);
}
