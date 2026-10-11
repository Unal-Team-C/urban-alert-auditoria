using Application.Auditoria;

namespace Application.Interfaces.Auditoria;

public interface IAuditStore
{
    Task<AuditEventResponse> PersistAsync(AuditEvent auditEvent, CancellationToken cancellationToken);
    Task<AuditTimeline> GetTimelineAsync(Guid reportId, long cursor, int limit, CancellationToken cancellationToken);
    Task<AuditIntegrityReport> VerifyAsync(Guid reportId, CancellationToken cancellationToken);
}
