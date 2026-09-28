using System.Text;
using Dochub.Api.Domain;
using Dochub.Api.Services;

namespace Dochub.Api.Tests;

/// <summary>Blob storage held in a dictionary, so a sync can be observed without Azure.</summary>
public class FakeBlobStorage : IBlobStorageService
{
    public Dictionary<string, byte[]> Blobs { get; } = [];
    public List<string> Deleted { get; } = [];
    public string ContainerName => "test-container";

    public string BuildPrefix(string teamSlug, string groupSlug, string artifactSlug, DateTimeOffset uploadedAtUtc) =>
        $"teams/{teamSlug}/groups/{groupSlug}/artifacts/{artifactSlug}/{uploadedAtUtc.UtcDateTime:yyyyMMdd'T'HHmmssfff}Z";

    public async Task<BlobUploadResult> UploadAsync(string blobPath, Stream content, string? contentType, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        Blobs[blobPath] = bytes;
        var checksum = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return new BlobUploadResult(blobPath, $"https://test/{blobPath}", bytes.Length, checksum);
    }

    public Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct) =>
        Task.FromResult<Stream>(new MemoryStream(Blobs[blobPath]));

    public Task DeleteAsync(string blobPath, CancellationToken ct)
    {
        Deleted.Add(blobPath);
        Blobs.Remove(blobPath);
        return Task.CompletedTask;
    }
}

/// <summary>Returns whatever the test says the remote folder currently holds.</summary>
public class StubExtractor(SourceType sourceType) : ISourceExtractor
{
    public SourceType SourceType { get; } = sourceType;
    public Dictionary<string, string> Files { get; set; } = [];

    public async IAsyncEnumerable<ExtractedDocument> ExtractAsync(
        ExtractionContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var (name, content) in Files)
        {
            yield return new ExtractedDocument(
                Name: name, RelativePath: name, SourceLocation: $"stub:/{name}",
                ContentType: "text/plain", ExternalId: null, SizeHint: content.Length,
                OpenAsync: _ => Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(content))));
            await Task.CompletedTask;
        }
    }
}

public class StubExtractorFactory(ISourceExtractor extractor) : ISourceExtractorFactory
{
    public ISourceExtractor Get(SourceType type) => extractor;
}

/// <summary>Round-trips tokens without needing the Data Protection stack.</summary>
public class PlainTextProtector : ITokenProtector
{
    public string Protect(string value) => value;
    public string Unprotect(string value) => value;
}

/// <summary>Hands back a fixed token; refreshing is covered by its own tests.</summary>
public class StubTokenProvider(string token = "stub-access-token") : ISourceTokenProvider
{
    public Task<string> GetAccessTokenAsync(SourceConnection connection, CancellationToken ct) =>
        Task.FromResult(token);
}

public class RecordingNotificationService : INotificationService
{
    public List<(string Type, string Title, string Body)> Raised { get; } = [];

    public Task RaiseAsync(Guid organizationId, Guid? userId, string type, string title, string body,
        NotificationSeverity severity, string? entityType, Guid? entityId, CancellationToken ct)
    {
        Raised.Add((type, title, body));
        return Task.CompletedTask;
    }
}


/// <summary>An in-memory queue, so a test can assert on what was sent and replay it.</summary>
public class FakeQueueClient : IQueueClient
{
    public record Sent(string Queue, string MessageId, string Subject, string Payload, string? SessionId);

    public List<Sent> Messages { get; } = [];

    public Task SendAsync(string queue, string messageId, string subject, string payload, string? sessionId, CancellationToken ct)
    {
        Messages.Add(new Sent(queue, messageId, subject, payload, sessionId));
        return Task.CompletedTask;
    }

    public Task<QueueLease?> ReceiveAsync(string queue, CancellationToken ct) =>
        Task.FromResult<QueueLease?>(null);

    public IEnumerable<Sent> On(string queue) => Messages.Where(m => m.Queue == queue);
}
