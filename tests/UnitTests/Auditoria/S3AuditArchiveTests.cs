using System.Net;
using System.Text.Json;
using Amazon.S3;
using Amazon.S3.Model;
using Application.Auditoria;
using Infrastructure.Archivado;
using Moq;

namespace UnitTests.Auditoria;

public sealed class S3AuditArchiveTests
{
    [Fact]
    public async Task ArchiveAsync_PutsComplianceObjectWhenHeadReturnsNotFound()
    {
        Mock<IAmazonS3> s3 = new();
        GetObjectMetadataRequest? headRequest = null;
        PutObjectRequest? putRequest = null;
        string? archivedJson = null;
        s3.Setup(client => client.GetObjectMetadataAsync(
                It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GetObjectMetadataRequest, CancellationToken>((request, _) => headRequest = request)
            .ThrowsAsync(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound });
        s3.Setup(client => client.PutObjectAsync(
                It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .Callback<PutObjectRequest, CancellationToken>((request, _) =>
            {
                putRequest = request;
                using StreamReader reader = new(request.InputStream, leaveOpen: true);
                archivedJson = reader.ReadToEnd();
            })
            .ReturnsAsync(new PutObjectResponse());
        AuditEventResponse record = CreateRecord();
        using S3AuditArchive archive = new(s3.Object, "audit-lock", 2555);

        await archive.ArchiveAsync(record, CancellationToken.None);

        Assert.Equal("audit-lock", headRequest!.BucketName);
        Assert.Equal($"events/2026-10-03/{record.EventId:D}.json", headRequest.Key);
        Assert.Equal(headRequest.Key, putRequest!.Key);
        Assert.Equal("application/json", putRequest.ContentType);
        Assert.Equal(ObjectLockMode.Compliance, putRequest.ObjectLockMode);
        Assert.NotNull(putRequest.ObjectLockRetainUntilDate);
        Assert.True(Convert.ToDateTime(putRequest.ObjectLockRetainUntilDate) > DateTime.UtcNow.AddDays(2554));
        using JsonDocument json = JsonDocument.Parse(archivedJson!);
        Assert.Equal(record.EventId.ToString(), json.RootElement.GetProperty("event").GetProperty("eventId").GetString());
        Assert.Equal(record.Hash, json.RootElement.GetProperty("hash").GetString());
        Assert.Equal(record.PreviousHash, json.RootElement.GetProperty("previousHash").GetString());
    }

    [Fact]
    public async Task ArchiveAsync_DoesNotOverwriteAnExistingObject()
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(client => client.GetObjectMetadataAsync(
                It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetObjectMetadataResponse());
        AuditEventResponse record = CreateRecord();
        using S3AuditArchive archive = new(s3.Object, "audit-lock", 2555);

        await archive.ArchiveAsync(record, CancellationToken.None);

        s3.Verify(client => client.PutObjectAsync(
            It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ArchiveAsync_PropagatesNonNotFoundS3Errors()
    {
        Mock<IAmazonS3> s3 = new();
        s3.Setup(client => client.GetObjectMetadataAsync(
                It.IsAny<GetObjectMetadataRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("access denied") { StatusCode = HttpStatusCode.Forbidden });
        using S3AuditArchive archive = new(s3.Object, "audit-lock", 2555);

        await Assert.ThrowsAsync<AmazonS3Exception>(() => archive.ArchiveAsync(CreateRecord(), CancellationToken.None));
    }

    private static AuditEventResponse CreateRecord()
    {
        Guid eventId = Guid.NewGuid();
        Guid reportId = Guid.NewGuid();
        Guid actorId = Guid.NewGuid();
        using JsonDocument payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            eventId,
            eventType = "reporte.creado",
            version = 1,
            occurredAt = "2026-10-03T12:00:00Z",
            correlationId = (Guid?)null,
            reportId,
            data = new { actorId, idCoordenada = Guid.NewGuid(), estado = "REPORTADO" }
        }));
        return new AuditEventResponse(7, eventId, "reporte.creado", 1,
            new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero), actorId, null,
            payload.RootElement.GetProperty("data").Clone(), "previous-hash", "current-hash",
            payload.RootElement.Clone());
    }
}
