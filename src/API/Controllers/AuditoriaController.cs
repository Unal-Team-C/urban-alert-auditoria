using System.Globalization;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace API.Controllers;

[ApiController]
[Authorize]
[Route("auditoria/reportes")]
public sealed class AuditoriaController(
    IAuditStore auditStore,
    IAuditAuthorizationService authorizationService) : ControllerBase
{
    [HttpGet("{reportId:guid}")]
    [ProducesResponseType<AuditTimeline>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTimeline(
        Guid reportId,
        [FromQuery] string? limit,
        [FromQuery] string? cursor,
        CancellationToken cancellationToken)
    {
        if (!int.TryParse(limit ?? "100", NumberStyles.None, CultureInfo.InvariantCulture, out int pageSize) ||
            pageSize is < 1 or > 500)
            return BadRequest(new { message = "limit debe estar entre 1 y 500." });
        if (!long.TryParse(cursor ?? "0", NumberStyles.None, CultureInfo.InvariantCulture, out long sequence))
            return BadRequest(new { message = "cursor debe ser un entero de secuencia." });

        AuditUserContext user;
        try
        {
            user = await authorizationService.ResolveUserAsync(User, cancellationToken);
        }
        catch (AuditAuthorizationException exception)
        {
            return StatusCode(exception.StatusCode, new { message = exception.Message });
        }

        if (user.Role is not ("ciudadano" or "gestor" or "admin"))
            return Forbid();
        int? accessError = await authorizationService.CheckReportAccessAsync(user, reportId, cancellationToken);
        if (accessError is not null)
            return StatusCode(accessError.Value);

        AuditTimeline timeline = await auditStore.GetTimelineAsync(reportId, sequence, pageSize, cancellationToken);
        return Ok(timeline);
    }

    [HttpGet("{reportId:guid}/integridad")]
    [ProducesResponseType<AuditIntegrityReport>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VerifyIntegrity(Guid reportId, CancellationToken cancellationToken)
    {
        AuditUserContext user;
        try
        {
            user = await authorizationService.ResolveUserAsync(User, cancellationToken);
        }
        catch (AuditAuthorizationException exception)
        {
            return StatusCode(exception.StatusCode, new { message = exception.Message });
        }

        if (user.Role is not ("gestor" or "admin"))
            return Forbid();
        int? accessError = await authorizationService.CheckReportAccessAsync(user, reportId, cancellationToken);
        if (accessError is not null)
            return StatusCode(accessError.Value);

        AuditIntegrityReport report = await auditStore.VerifyAsync(reportId, cancellationToken);
        return Ok(report);
    }
}
