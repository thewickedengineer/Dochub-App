using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

public record BlobUploadResult(string BlobPath, string BlobUrl, long SizeBytes, string ChecksumSha256);

public interface IBlobStorageService
{
    /// <summary>teams/{team}/groups/{group}/artifacts/{artifact}/{yyyyMMddTHHmmssfffZ}</summary>
    string BuildPrefix(string teamSlug, string groupSlug, string artifactSlug, DateTimeOffset uploadedAtUtc);
    string ContainerName { get; }
    Task<BlobUploadResult> UploadAsync(string blobPath, Stream content, string? contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct);
    Task DeleteAsync(string blobPath, CancellationToken ct);
}

public class BlobStorageService : IBlobStorageService
{
    private readonly BlobContainerClient _container;
    private readonly ILogger<BlobStorageService> _log;
    private int _containerReady;

    public BlobStorageService(IOptions<AzureStorageOptions> options, ILogger<BlobStorageService> log)
    {
        _log = log;
        var o = options.Value;
        var service = string.IsNullOrWhiteSpace(o.AccountUrl)
            ? new BlobServiceClient(o.ConnectionString)
            : new BlobServiceClient(new Uri(o.AccountUrl), new DefaultAzureCredential());
        _container = service.GetBlobContainerClient(o.Container);
        ContainerName = o.Container;
    }

    public string ContainerName { get; }

    public string BuildPrefix(string teamSlug, string groupSlug, string artifactSlug, DateTimeOffset uploadedAtUtc) =>
        $"teams/{teamSlug}/groups/{groupSlug}/artifacts/{artifactSlug}/{uploadedAtUtc.UtcDateTime:yyyyMMdd'T'HHmmssfff}Z";

    public async Task<BlobUploadResult> UploadAsync(string blobPath, Stream content, string? contentType, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        var blob = _container.GetBlobClient(blobPath);

        // Hash while buffering so the checksum costs one pass, not a second read.
        using var buffer = new MemoryStream();
        using var sha = System.Security.Cryptography.SHA256.Create();
        await using (var hashing = new System.Security.Cryptography.CryptoStream(buffer, sha, System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(hashing, ct);
            await hashing.FlushFinalBlockAsync(ct);
        }
        buffer.Position = 0;
        var checksum = Convert.ToHexString(sha.Hash!).ToLowerInvariant();

        await blob.UploadAsync(buffer, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = contentType ?? "application/octet-stream" },
            Metadata = new Dictionary<string, string> { ["sha256"] = checksum }
        }, ct);

        _log.LogInformation("Uploaded {Path} ({Bytes} bytes) to container {Container}", blobPath, buffer.Length, ContainerName);
        return new BlobUploadResult(blobPath, blob.Uri.ToString(), buffer.Length, checksum);
    }

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        return await _container.GetBlobClient(blobPath).OpenReadAsync(cancellationToken: ct);
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct) =>
        await _container.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _containerReady, 1, 0) != 0) return;
        try { await _container.CreateIfNotExistsAsync(cancellationToken: ct); }
        catch { Interlocked.Exchange(ref _containerReady, 0); throw; }
    }
}
