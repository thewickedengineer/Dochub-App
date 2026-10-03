using System.Text.Json;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

public record SourceSubmission(
    SourceType SourceType,
    string SourceReference,
    Guid? SourceConnectionId,
    JsonElement? Options,
    IReadOnlyList<(string Name, string RelativePath, long? SizeBytes, string? ContentType)> DeclaredDocuments);

/// <summary>
/// Step 2 of the pipeline: writes the source_documents row and puts the source
/// on the extract queue. Used by the Process button and by recurring syncs, so
/// both enter the pipeline through exactly the same door.
/// </summary>
public interface ISourceSubmissionService
{
    Task<SourceDocument> SubmitAsync(
        Artifact artifact, Guid organizationId, Guid requestedByUserId,
        SourceSubmission submission, Guid? syncScheduleId, CancellationToken ct);
}

public class SourceSubmissionService(
    DochubDbContext db,
    IBlobStorageService blobs,
    IQueueClient queues,
    IOptions<ServiceBusOptions> serviceBus,
    ILogger<SourceSubmissionService> log) : ISourceSubmissionService
{
    public async Task<SourceDocument> SubmitAsync(
        Artifact artifact, Guid organizationId, Guid requestedByUserId,
        SourceSubmission submission, Guid? syncScheduleId, CancellationToken ct)
    {
        var group = artifact.Group;
        var team = group.Team;
        var requestedAt = DateTimeOffset.UtcNow;
        var prefix = blobs.BuildPrefix(team.Slug, group.Slug, artifact.Slug, requestedAt);

        // ── Status: Request Upload ────────────────────────────────────────────
        var source = new SourceDocument
        {
            Reference = await NextReferenceAsync(ct),
            OrganizationId = organizationId,
            ArtifactId = artifact.Id,
            SourceType = submission.SourceType,
            SourceConnectionId = submission.SourceConnectionId,
            SourceReference = string.IsNullOrWhiteSpace(submission.SourceReference)
                ? submission.SourceType.Label()
                : submission.SourceReference.Trim(),
            SourceOptions = submission.Options is { } options
                ? JsonDocument.Parse(options.GetRawText())
                : null,
            RequestedByUserId = requestedByUserId,
            SyncScheduleId = syncScheduleId,
            Status = SourceDocumentStatus.RequestUpload,
            BlobContainer = blobs.ContainerName,
            BlobPrefix = prefix,
            RequestedAt = requestedAt,
            TotalDocuments = submission.DeclaredDocuments.Count
        };
        db.SourceDocuments.Add(source);
        artifact.Status = ArtifactStatus.Pending;

        // Local files are known up front, so their rows exist before extraction
        // and the caller sees a document list immediately.
        foreach (var declared in submission.DeclaredDocuments)
        {
            db.UploadedDocuments.Add(new UploadedDocument
            {
                SourceDocument = source,
                ArtifactId = artifact.Id,
                SourceType = submission.SourceType,
                Name = declared.Name,
                RelativePath = string.IsNullOrWhiteSpace(declared.RelativePath) ? declared.Name : declared.RelativePath,
                SourceLocation = source.SourceReference,
                ContentType = declared.ContentType ?? MimeTypes.For(declared.Name),
                SizeBytes = declared.SizeBytes ?? 0,
                Status = DocumentStatus.Pending
            });
        }

        await db.SaveChangesAsync(ct);

        // ── Queue 1: everything the extractor needs to fetch this source ──────
        var payload = new SourceUploadRequestedEvent(
            source.Id, source.Reference, artifact.Id, organizationId, artifact.Name,
            team.Name, group.Name, source.SourceType.ToString(), source.SourceReference,
            source.SourceConnectionId,
            source.SourceOptions is null ? null : JsonDocument.Parse(source.SourceOptions.RootElement.GetRawText()).RootElement,
            requestedByUserId, requestedAt, blobs.ContainerName, prefix);

        await queues.SendAsync(serviceBus.Value.ExtractQueue,
            $"extract-{source.Id:n}", DochubEvents.SourceUploadRequested,
            DochubEvents.Serialize(payload), artifact.Id.ToString(), ct);

        log.LogInformation("{Reference} requested upload for {Artifact} → {Queue}",
            source.Reference, artifact.Name, serviceBus.Value.ExtractQueue);

        return source;
    }

    /// <summary>
    /// Pulls the next display id from a Postgres sequence, so two people pressing
    /// Process at once can never be handed the same SRC- number.
    /// </summary>
    private async Task<string> NextReferenceAsync(CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT nextval('dochub.source_document_number')";
        await db.Database.OpenConnectionAsync(ct);
        try { return $"SRC-{await command.ExecuteScalarAsync(ct)}"; }
        finally { await db.Database.CloseConnectionAsync(); }
    }
}
