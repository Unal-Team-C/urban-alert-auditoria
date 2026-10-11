using Application.Auditoria;

namespace Application.Interfaces.Auditoria;

public interface IAuditArchive
{
    Task ArchiveAsync(AuditEventResponse auditEvent, CancellationToken cancellationToken);
}
