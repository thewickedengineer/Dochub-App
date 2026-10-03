using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

/// <param name="ContentMd5">
/// The Content-MD5 Azure reports for the stored blob, base64 as Azure writes it.
/// This is the hash recurring updates compare against to decide whether a file
/// changed — it is what the store actually holds, not what we meant to send.
/// </param>
public record BlobUploadResult(
    string BlobPath, string BlobUrl, long SizeBytes, string ChecksumSha256,
    string ContentMd5, string ETag, DateTimeOffset LastModified);

public interface IBlobStorageService
{
    /// <summary>teams/{team}/groups/{group}/artifacts/{artifact}/{yyyyMMddTHHmmssfffZ}</summary>
    string BuildPrefix(string teamSlug, string groupSlug, string artifactSlug, DateTimeOffset uploadedAtUtc);
    string ContainerName { get; }
    Task<BlobUploadResult> UploadAsync(string blobPath, Stream content, string? contentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct);
    Task DeleteAsync(string blobPath, CancellationToken ct);
    /// <summary>Deletes every blob under a prefix (one upload's folder); returns how many.</summary>
    Task<int> DeletePrefixAsync(string prefix, CancellationToken ct);
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

        // One pass computes both hashes while buffering.
        using var buffer = new MemoryStream();
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var md5 = System.Security.Cryptography.MD5.Create();
        await using (var shaStream = new System.Security.Cryptography.CryptoStream(
                         buffer, sha256, System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
        await using (var md5Stream = new System.Security.Cryptography.CryptoStream(
                         shaStream, md5, System.Security.Cryptography.CryptoStreamMode.Write, leaveOpen: true))
        {
            await content.CopyToAsync(md5Stream, ct);
            await md5Stream.FlushFinalBlockAsync(ct);
        }
        buffer.Position = 0;
        var checksum = Convert.ToHexString(sha256.Hash!).ToLowerInvariant();

        // Sending Content-MD5 makes Azure verify the bytes on arrival and store
        // the hash on the blob, including for block uploads where the upload
        // response itself carries no hash.
        var response = await blob.UploadAsync(buffer, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders
            {
                ContentType = contentType ?? "application/octet-stream",
                ContentHash = md5.Hash
            },
            Metadata = new Dictionary<string, string> { ["sha256"] = checksum }
        }, ct);

        var info = response.Value;
        var returnedHash = info.ContentHash;
        var etag = info.ETag.ToString();
        var lastModified = info.LastModified;

        if (returnedHash is null || returnedHash.Length == 0)
        {
            // Large uploads commit a block list, whose response has no MD5; the
            // blob's own properties are the authoritative copy.
            var properties = (await blob.GetPropertiesAsync(cancellationToken: ct)).Value;
            returnedHash = properties.ContentHash;
            etag = properties.ETag.ToString();
            lastModified = properties.LastModified;
        }

        if (returnedHash is null || returnedHash.Length == 0)
            throw new InvalidOperationException(
                $"Blob storage returned no content hash for {blobPath}; change detection would be unreliable.");

        var contentMd5 = Convert.ToBase64String(returnedHash);
        _log.LogInformation("Uploaded {Path} ({Bytes} bytes, md5 {Md5}) to {Container}",
            blobPath, buffer.Length, contentMd5, ContainerName);

        return new BlobUploadResult(blobPath, blob.Uri.ToString(), buffer.Length, checksum,
            contentMd5, etag, lastModified);
    }

    public async Task<Stream> OpenReadAsync(string blobPath, CancellationToken ct)
    {
        await EnsureContainerAsync(ct);
        return await _container.GetBlobClient(blobPath).OpenReadAsync(cancellationToken: ct);
    }

    public async Task DeleteAsync(string blobPath, CancellationToken ct) =>
        await _container.GetBlobClient(blobPath).DeleteIfExistsAsync(cancellationToken: ct);

    public async Task<int> DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        // A trailing slash so "…/20261002T1200" never matches "…/20261002T12000".
        var folder = prefix.TrimEnd('/') + "/";
        var deleted = 0;
        try
        {
            await foreach (var item in _container.GetBlobsAsync(Azure.Storage.Blobs.Models.BlobTraits.None, Azure.Storage.Blobs.Models.BlobStates.None, folder, ct))
                if ((await _container.GetBlobClient(item.Name).DeleteIfExistsAsync(cancellationToken: ct)).Value)
                    deleted++;
        }
        catch (Azure.RequestFailedException e) when (e.Status == 404)
        {
            // No container yet means nothing was ever stored.
        }
        return deleted;
    }

    private async Task EnsureContainerAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _containerReady, 1, 0) != 0) return;
        try { await _container.CreateIfNotExistsAsync(cancellationToken: ct); }
        catch { Interlocked.Exchange(ref _containerReady, 0); throw; }
    }
}
