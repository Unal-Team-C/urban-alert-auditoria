using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.S3;
using Amazon.S3.Model;
using Application.Auditoria;
using Application.Interfaces.Auditoria;

namespace Infrastructure.Archivado;

public sealed class NoOpAuditArchive : IAuditArchive
{
    public Task ArchiveAsync(AuditEventResponse auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class S3AuditArchive(IAmazonS3 s3, string bucket, int retentionDays) : IAuditArchive, IDisposable
{
    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    public async Task ArchiveAsync(AuditEventResponse auditEvent, CancellationToken cancellationToken)
    {
        string occurredAt = auditEvent.Payload.GetProperty("occurredAt").GetString()
                            ?? throw new InvalidOperationException("El evento no tiene occurredAt.");
        string key = $"events/{occurredAt[..10]}/{auditEvent.EventId:D}.json";

        try
        {
            await s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = bucket,
                Key = key
            }, cancellationToken);
            return;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
        }

        S3ArchiveDocument archive = new(auditEvent.Payload, auditEvent.Hash, auditEvent.PreviousHash);
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(archive, _jsonOptions);
        await using MemoryStream stream = new(content);
        await s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = bucket,
            Key = key,
            InputStream = stream,
            ContentType = "application/json",
            ObjectLockMode = ObjectLockMode.Compliance,
            ObjectLockRetainUntilDate = DateTime.UtcNow.AddDays(retentionDays)
        }, cancellationToken);
    }

    public void Dispose() => s3.Dispose();

    private sealed record S3ArchiveDocument(
        [property: JsonPropertyName("event")] JsonElement Event,
        [property: JsonPropertyName("hash")] string Hash,
        [property: JsonPropertyName("previousHash")] string PreviousHash);
}
