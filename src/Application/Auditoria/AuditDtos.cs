using System.Text.Json;
using System.Text.Json.Serialization;

namespace Application.Auditoria;

public sealed record AuditUserContext(Guid Subject, string Role);

public sealed record AuditEvent(
    Guid EventId,
    string EventType,
    int Version,
    DateTimeOffset OccurredAt,
    Guid? CorrelationId,
    Guid? ReportId,
    Guid ActorId,
    JsonElement Data,
    JsonElement Payload);

public sealed record AuditEventResponse(
    long Sequence,
    Guid EventId,
    string EventType,
    int Version,
    DateTimeOffset OccurredAt,
    Guid ActorId,
    Guid? CorrelationId,
    JsonElement Data,
    string PreviousHash,
    string Hash,
    [property: JsonIgnore] JsonElement Payload);

public sealed record AuditTimeline(Guid ReportId, IReadOnlyList<AuditEventResponse> Items, string? NextCursor);

public sealed record AuditIntegrityReport(
    Guid ReportId,
    bool Verified,
    int ReportEventCount,
    int ChainEventCount,
    bool HeadMatches,
    long? BrokenSequence,
    DateTimeOffset CheckedAt);
