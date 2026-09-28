using System.Text.Json;

namespace Dochub.Api.Services;

/// <summary>The two messages that move work through the pipeline.</summary>
public static class DochubEvents
{
    /// <summary>Queue 1 — a source is ready to have its files moved into blob storage.</summary>
    public const string SourceUploadRequested = "dochub.source.upload.requested";

    /// <summary>Queue 2 — files are in blob storage and are ready to be vectorized.</summary>
    public const string DocumentsProcessRequested = "dochub.documents.process.requested";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Options);
    public static T Deserialize<T>(string payload) => JsonSerializer.Deserialize<T>(payload, Options)
        ?? throw new InvalidOperationException($"Could not read a {typeof(T).Name} from the message body.");
}

/// <summary>
/// Everything the extractor needs to fetch a source, with no second database
/// read required: where it lives, which grant to use and where to put it.
/// </summary>
public record SourceUploadRequestedEvent(
    Guid SourceDocumentId,
    string Reference,
    Guid ArtifactId,
    Guid OrganizationId,
    string ArtifactName,
    string TeamName,
    string GroupName,
    string SourceType,
    string SourceReference,
    Guid? SourceConnectionId,
    JsonElement? SourceOptions,
    Guid RequestedByUserId,
    DateTimeOffset RequestedAt,
    string BlobContainer,
    string BlobPrefix);

/// <summary>
/// Handed to the vector service once every file is in blob storage. It carries
/// container, path and checksum per document — never the bytes and never a
/// token, so the consumer fetches with its own credentials.
/// </summary>
public record DocumentsProcessRequestedEvent(
    Guid SourceDocumentId,
    string Reference,
    Guid ArtifactId,
    Guid OrganizationId,
    string ArtifactName,
    string TeamName,
    string GroupName,
    string SourceType,
    string BlobContainer,
    string BlobPrefix,
    int DocumentCount,
    DateTimeOffset UploadedAt,
    IReadOnlyList<ProcessableDocument> Documents);

public record ProcessableDocument(
    Guid DocumentId,
    string Name,
    string RelativePath,
    string BlobPath,
    string BlobUrl,
    long SizeBytes,
    string? ContentType,
    string? ChecksumSha256,
    int Revision);
