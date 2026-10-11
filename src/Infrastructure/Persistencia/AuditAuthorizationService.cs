using System.Security.Claims;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Infrastructure.Persistencia;

public sealed class AuditAuthorizationService(IConfiguration configuration) : IAuditAuthorizationService
{
    public Task<AuditUserContext> ResolveUserAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        string? subjectClaim = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(subjectClaim, out Guid subject))
            throw new AuditAuthorizationException(StatusCodes.Status401Unauthorized, "El token no contiene un sub válido.");

        string role = GetRoleFromClaims(principal);
        if (role is not ("ciudadano" or "gestor" or "admin"))
            throw new AuditAuthorizationException(StatusCodes.Status403Forbidden, "El usuario no tiene un rol habilitado.");

        return Task.FromResult(new AuditUserContext(subject, role));
    }

    public async Task<int?> CheckReportAccessAsync(AuditUserContext user, Guid reportId, CancellationToken cancellationToken)
    {
        string connectionString = configuration.GetConnectionString("CorePostgres")
                                  ?? throw new InvalidOperationException("Falta configurar ConnectionStrings:CorePostgres.");
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = new(
            "SELECT \"IdUsuario\", \"IdResponsable\" FROM \"Reportes\" WHERE \"Id\" = @reportId", connection);
        command.Parameters.AddWithValue("reportId", reportId);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return StatusCodes.Status404NotFound;

        Guid ownerId = reader.GetGuid(0);
        Guid? responsibleId = reader.IsDBNull(1) ? null : reader.GetGuid(1);
        if (user.Role == "ciudadano" && ownerId != user.Subject)
            return StatusCodes.Status404NotFound;
        if (user.Role == "gestor" && responsibleId != user.Subject)
            return StatusCodes.Status403Forbidden;
        return null;
    }

    private static string GetRoleFromClaims(ClaimsPrincipal principal)
    {
        HashSet<string> groups = principal.FindAll("cognito:groups").Select(claim => claim.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (groups.Contains("admin")) return "admin";
        if (groups.Contains("gestor")) return "gestor";
        return "ciudadano";
    }
}
