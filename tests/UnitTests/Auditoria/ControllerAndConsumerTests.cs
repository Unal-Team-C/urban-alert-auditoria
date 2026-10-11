using System.Security.Claims;
using System.Text.Json;
using API.Controllers;
using Application.Auditoria;
using Application.Interfaces.Auditoria;
using Application.Reportes.Eventos;
using Domain.Auditoria;
using Domain.Reportes;
using Infrastructure.Mensajeria;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace UnitTests.Auditoria;

public sealed class ControllerAndConsumerTests
{
    [Fact]
    public async Task Timeline_RejectsInvalidLimitBeforeCallingDependencies()
    {
        Mock<IAuditStore> store = new();
        Mock<IAuditAuthorizationService> authorization = new();
        AuditoriaController controller = CreateController(store.Object, authorization.Object);

        IActionResult result = await controller.GetTimeline(Guid.NewGuid(), "501", "0", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        authorization.Verify(service => service.ResolveUserAsync(
            It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(service => service.GetTimelineAsync(
            It.IsAny<Guid>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Timeline_ReturnsStorePageAfterAuthorization()
    {
        Guid subject = Guid.NewGuid();
        Guid reportId = Guid.NewGuid();
        AuditTimeline timeline = new(reportId, [], null);
        Mock<IAuditStore> store = new();
        Mock<IAuditAuthorizationService> authorization = new();
        authorization.Setup(service => service.ResolveUserAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditUserContext(subject, "ciudadano"));
        authorization.Setup(service => service.CheckReportAccessAsync(
                It.IsAny<AuditUserContext>(), reportId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((int?)null);
        store.Setup(service => service.GetTimelineAsync(reportId, 0, 1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(timeline);
        AuditoriaController controller = CreateController(store.Object, authorization.Object, subject);

        IActionResult result = await controller.GetTimeline(reportId, "1", "0", CancellationToken.None);

        OkObjectResult response = Assert.IsType<OkObjectResult>(result);
        Assert.Same(timeline, response.Value);
        authorization.Verify(service => service.CheckReportAccessAsync(
            It.IsAny<AuditUserContext>(), reportId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerifyIntegrity_RejectsCitizenBeforeReadingStore()
    {
        Guid subject = Guid.NewGuid();
        Mock<IAuditStore> store = new();
        Mock<IAuditAuthorizationService> authorization = new();
        authorization.Setup(service => service.ResolveUserAsync(
                It.IsAny<ClaimsPrincipal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditUserContext(subject, "ciudadano"));
        AuditoriaController controller = CreateController(store.Object, authorization.Object, subject);

        IActionResult result = await controller.VerifyIntegrity(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        store.Verify(service => service.VerifyAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ReporteCreadoConsumer_PersistsThenArchivesNormalizedMessage()
    {
        Guid eventId = Guid.NewGuid();
        Guid reportId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        Guid correlationId = Guid.NewGuid();
        ReporteCreadoEvent message = new(eventId,
            new ReporteEventoDto(reportId, EstadoReporte.Reportado, Guid.NewGuid(), actorId,
                new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc)));
        AuditEvent normalized = DotNetAuditEventFactory.FromReporteCreado(message, correlationId);
        AuditEventResponse persisted = ToResponse(normalized);
        Mock<IAuditStore> store = new();
        Mock<IAuditArchive> archive = new();
        store.Setup(service => service.PersistAsync(
                It.Is<AuditEvent>(auditEvent => auditEvent.EventId == eventId &&
                                                auditEvent.ReportId == reportId &&
                                                auditEvent.CorrelationId == correlationId),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(persisted);
        archive.Setup(service => service.ArchiveAsync(persisted, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Mock<ConsumeContext<ReporteCreadoEvent>> context = new();
        context.SetupGet(item => item.Message).Returns(message);
        context.SetupGet(item => item.CorrelationId).Returns(correlationId);
        context.SetupGet(item => item.CancellationToken).Returns(CancellationToken.None);
        ReporteCreadoConsumer consumer = new(store.Object, archive.Object, NullLogger<ReporteCreadoConsumer>.Instance);

        await consumer.Consume(context.Object);

        store.Verify(service => service.PersistAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        archive.Verify(service => service.ArchiveAsync(persisted, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ReporteCreadoConsumer_PropagatesArchiveFailureForMassTransitRetry()
    {
        ReporteCreadoEvent message = new(Guid.NewGuid(),
            new ReporteEventoDto(Guid.NewGuid(), EstadoReporte.Reportado, Guid.NewGuid(), Guid.NewGuid(),
                new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc)));
        AuditEvent normalized = DotNetAuditEventFactory.FromReporteCreado(message, null);
        AuditEventResponse persisted = ToResponse(normalized);
        Mock<IAuditStore> store = new();
        Mock<IAuditArchive> archive = new();
        store.Setup(service => service.PersistAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(persisted);
        archive.Setup(service => service.ArchiveAsync(persisted, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("S3 unavailable"));
        Mock<ConsumeContext<ReporteCreadoEvent>> context = new();
        context.SetupGet(item => item.Message).Returns(message);
        context.SetupGet(item => item.CorrelationId).Returns((Guid?)null);
        context.SetupGet(item => item.CancellationToken).Returns(CancellationToken.None);
        ReporteCreadoConsumer consumer = new(store.Object, archive.Object, NullLogger<ReporteCreadoConsumer>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Consume(context.Object));

        store.Verify(service => service.PersistAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()), Times.Once);
        archive.Verify(service => service.ArchiveAsync(persisted, It.IsAny<CancellationToken>()), Times.Once);
    }

    private static AuditoriaController CreateController(
        IAuditStore store,
        IAuditAuthorizationService authorization,
        Guid? subject = null)
    {
        AuditoriaController controller = new(store, authorization)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = subject is null
                        ? new ClaimsPrincipal(new ClaimsIdentity())
                        : new ClaimsPrincipal(new ClaimsIdentity(
                            [new Claim("sub", subject.Value.ToString("D"))], "test"))
                }
            }
        };
        return controller;
    }

    private static AuditEventResponse ToResponse(AuditEvent auditEvent)
    {
        using JsonDocument payload = JsonDocument.Parse(auditEvent.Payload.GetRawText());
        return new AuditEventResponse(1, auditEvent.EventId, auditEvent.EventType, auditEvent.Version,
            auditEvent.OccurredAt, auditEvent.ActorId, auditEvent.CorrelationId,
            auditEvent.Data.Clone(), AuditHash.GenesisHash, new string('a', 64), payload.RootElement.Clone());
    }
}
