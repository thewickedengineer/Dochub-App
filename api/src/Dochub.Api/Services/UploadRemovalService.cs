using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

/// <summary>The RAG platform's purge call: an upload's documents leave the index.</summary>
public interface IRagPurgeClient
{
    /// <summary>Per Dochub document id: "removed", "kept" (its indexed copy is exactly keep_version_id) or "absent".</summary>
    Task<IReadOnlyDictionary<Guid, string>> PurgeAsync(Guid organizationId, IReadOnlyList<(Guid DocumentId, Guid? KeepVersionId)> documents, CancellationToken ct);
}

public sealed class RagPurgeClient(IHttpClientFactory http, IOptions<IngestionOptions> ingestion, IOptions<RagOptions> rag) : IRagPurgeClient
{
    public async Task<IReadOnlyDictionary<Guid, string>> PurgeAsync(
        Guid organizationId, IReadOnlyList<(Guid DocumentId, Guid? KeepVersionId)> documents, CancellationToken ct)
    {
        if (documents.Count == 0) return new Dictionary<Guid, string>();
        var body = new JsonObject
        {
            ["tenant_id"] = organizationId.ToString(),
            ["documents"] = new JsonArray(documents.Select(d => (JsonNode)new JsonObject
            {
                ["dochub_document_id"] = d.DocumentId.ToString(),
                ["keep_version_id"] = d.KeepVersionId?.ToString()
            }).ToArray())
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(rag.Value.PurgeTimeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Post, "admin/purge") { Content = JsonContent.Create(body) };
        request.Headers.Add(IngestionEndpoints.ServiceKeyHeader, ingestion.Value.ServiceKey);
        using var response = await http.CreateClient("rag").SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token)
            ?? throw new HttpRequestException("The RAG platform returned an empty purge result.");
        return result["documents"]!.AsArray().ToDictionary(
            d => Guid.Parse((string)d!["dochub_document_id"]!), d => (string)d!["outcome"]!);
    }
}

public sealed record RemovalResult(string Reference, int DocumentsRemoved, int DocumentsRestored, int BlobsDeleted, int Reindexed);

public sealed class RemovalBlockedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Undoes an upload. Two uses:
///
/// <list type="bullet">
/// <item><b>On a failed upload to Blob storage</b> (automatic): every file this upload
/// stored is deleted from Blob storage, its version rows go, documents it updated
/// are put back on their previous version, and documents it created stay listed as
/// failed (no file behind them) so the reason is still visible.</item>
/// <item><b>Remove</b> (the user's choice, for a failed or partly failed upload):
/// graceful and in order — stop queued work, take its documents out of the search
/// index, delete its files, then delete or roll back every row it wrote, in one
/// transaction. If a step fails the upload stays "Removing" and pressing Remove
/// again carries on from there; nothing is left half-deleted.</item>
/// </list>
///
/// "What this upload wrote" is read from <c>document_versions</c>: a document whose
/// every version came from this upload was created by it; one with an earlier
/// version from another upload was only updated by it and is restored, not deleted.
/// </summary>
public sealed class UploadRemovalService(
    DochubDbContext db,
    IBlobStorageService blobs,
    IRagPurgeClient rag,
    IQueueClient queues,
    IOptions<ServiceBusOptions> serviceBus,
    INotificationService notifications,
    ILogger<UploadRemovalService> log)
{
    public static readonly SourceDocumentStatus[] Removable =
        [SourceDocumentStatus.Failed, SourceDocumentStatus.PartiallyFailed, SourceDocumentStatus.Cancelled, SourceDocumentStatus.Removing];

    private sealed record Touched(UploadedDocument Document, DocumentVersion? Previous);

    private async Task<List<Touched>> TouchedAsync(SourceDocument source, CancellationToken ct)
    {
        var ownVersions = await db.DocumentVersions.Where(v => v.SourceDocumentId == source.Id)
            .Select(v => v.UploadedDocumentId).ToListAsync(ct);
        var ids = ownVersions.Concat(await db.UploadedDocuments.Where(d => d.SourceDocumentId == source.Id)
            .Select(d => d.Id).ToListAsync(ct)).Distinct().ToList();

        var documents = await db.UploadedDocuments.Where(d => ids.Contains(d.Id)).ToListAsync(ct);
        var previous = (await db.DocumentVersions
                .Where(v => ids.Contains(v.UploadedDocumentId) && v.SourceDocumentId != source.Id)
                .ToListAsync(ct))
            .GroupBy(v => v.UploadedDocumentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(v => v.Revision).First());
        return documents.Select(d => new Touched(d, previous.GetValueOrDefault(d.Id))).ToList();
    }

    private static void Restore(UploadedDocument document, DocumentVersion previous, DocumentStatus status)
    {
        document.SourceDocumentId = previous.SourceDocumentId;
        document.Revision = previous.Revision;
        document.Name = previous.Name;
        document.RelativePath = previous.RelativePath;
        document.SourceLocation = previous.SourceLocation;
        document.ExternalId = previous.ExternalId;
        document.ContentType = previous.ContentType;
        document.SizeBytes = previous.SizeBytes;
        document.BlobPath = previous.BlobPath;
        document.BlobUrl = previous.BlobUrl;
        document.BlobETag = previous.BlobETag;
        document.ContentMd5 = previous.ContentMd5;
        document.ChecksumSha256 = previous.ContentSha256;
        document.BlobUploadedAt = previous.UploadedAt;
        document.CurrentVersionId = previous.Id;
        document.Status = status;
        document.Error = null;
        document.UpdatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>The automatic half: called when an upload to Blob storage fails.</summary>
    public async Task<int> RollBackStoredFilesAsync(SourceDocument source, string reason, CancellationToken ct)
    {
        var touched = await TouchedAsync(source, ct);
        foreach (var (document, previous) in touched)
        {
            if (previous is not null)
            {
                // Its earlier version was never sent anywhere, so it is still what is indexed.
                Restore(document, previous, DocumentStatus.Indexed);
                continue;
            }
            document.Status = DocumentStatus.Failed;
            document.Error ??= reason;
            document.BlobPath = document.BlobUrl = document.ContentMd5 = document.ChecksumSha256 = document.BlobETag = null;
            document.CurrentVersionId = null;
            document.BlobUploadedAt = null;
            document.UpdatedAt = DateTimeOffset.UtcNow;
        }
        db.DocumentVersions.RemoveRange(db.DocumentVersions.Where(v => v.SourceDocumentId == source.Id));
        await db.SaveChangesAsync(ct);

        var deleted = source.BlobPrefix is null ? 0 : await blobs.DeletePrefixAsync(source.BlobPrefix, ct);
        log.LogInformation("{Reference}: upload failed, removed {Count} stored file(s) from Blob storage", source.Reference, deleted);
        return deleted;
    }

    /// <summary>The user's Remove: everything this upload put anywhere goes.</summary>
    public async Task<RemovalResult> RemoveAsync(Guid sourceId, Guid organizationId, CancellationToken ct)
    {
        var source = await db.SourceDocuments
            .Include(s => s.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .FirstOrDefaultAsync(s => s.Id == sourceId && s.OrganizationId == organizationId, ct)
            ?? throw new RemovalBlockedException("not_found", "No such upload.");

        if (!Removable.Contains(source.Status))
            throw new RemovalBlockedException("not_removable", source.Status is SourceDocumentStatus.Processed
                ? "This upload succeeded. Only failed or partly failed uploads can be removed."
                : $"This upload is still {source.Status.ToLabel().ToLowerInvariant()}. Remove it once it has finished or failed.");

        // 1. Stop queued work. A message being worked on right now cannot be pulled
        //    back safely, so wait for it; waiting ones are simply withdrawn.
        var key = source.Id.ToString();
        var inFlight = await db.QueueMessages.FromSqlInterpolated(
                $"SELECT * FROM dochub.queue_messages WHERE status = 1 AND payload->>'sourceDocumentId' = {key}")
            .AnyAsync(ct);
        if (inFlight)
            throw new RemovalBlockedException("in_progress", "Part of this upload is being worked on right now. Try again in a minute.");
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM dochub.queue_messages WHERE status = 0 AND payload->>'sourceDocumentId' = {key}", ct);

        source.Status = SourceDocumentStatus.Removing;
        source.Error = null;
        await db.SaveChangesAsync(ct);

        // 2. Out of the search index. Updated documents keep their indexed copy when
        //    it is still the earlier version; otherwise it goes and is rebuilt below.
        var touched = await TouchedAsync(source, ct);
        IReadOnlyDictionary<Guid, string> outcomes;
        try
        {
            outcomes = await rag.PurgeAsync(organizationId,
                touched.Select(t => (t.Document.Id, t.Previous?.Id)).ToList(), ct);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            source.Error = "Removal paused: the knowledge base could not be reached. Press Remove again to carry on.";
            await db.SaveChangesAsync(CancellationToken.None);
            log.LogWarning(e, "{Reference}: purge failed; left in Removing", source.Reference);
            throw new RemovalBlockedException("rag_unavailable", source.Error);
        }

        // 3. Its files. The prefix is this upload's own folder, so nothing else lives there.
        var blobsDeleted = source.BlobPrefix is null ? 0 : await blobs.DeletePrefixAsync(source.BlobPrefix, ct);

        // 4. Every row, in one transaction.
        var reindex = new List<(UploadedDocument Document, DocumentVersion Version)>();
        var removed = 0;
        // The connection retries transient failures, so the transaction has to run as
        // one retriable unit: on a retry everything is re-read and re-applied.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            reindex.Clear();
            removed = 0;
            db.ChangeTracker.Clear();
            var current = await db.SourceDocuments.Include(s => s.Artifact)
                .FirstAsync(s => s.Id == source.Id, ct);
            var rows = await TouchedAsync(current, ct);

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            foreach (var (document, previous) in rows)
            {
                if (previous is null)
                {
                    db.UploadedDocuments.Remove(document);
                    removed++;
                    continue;
                }
                var kept = outcomes.GetValueOrDefault(document.Id) == "kept";
                Restore(document, previous, kept ? DocumentStatus.Indexed : DocumentStatus.Uploaded);
                if (!kept) reindex.Add((document, previous));
            }
            db.DocumentVersions.RemoveRange(db.DocumentVersions.Where(v => v.SourceDocumentId == current.Id));
            db.Notifications.RemoveRange(db.Notifications.Where(n => n.EntityId == current.Id));
            await db.RecurringSyncSchedules.Where(s => s.LastSourceDocumentId == current.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastSourceDocumentId, (Guid?)null), ct);
            await db.SaveChangesAsync(ct);

            db.SourceDocuments.Remove(current);
            await db.SaveChangesAsync(ct);

            var states = await db.UploadedDocuments.Where(d => d.ArtifactId == current.ArtifactId).Select(d => d.Status).ToListAsync(ct);
            current.Artifact.Status = IngestionEndpoints.ArtifactStatusFrom(states);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        });

        // 5. A restored document whose earlier version had already been replaced in the
        //    index is indexed again, under the upload that version came from.
        foreach (var group in reindex.GroupBy(r => r.Version.SourceDocumentId))
        {
            var team = source.Artifact.Group.Team;
            var documents = group.Select(r => new ProcessableDocument(
                r.Document.Id, r.Document.Name, r.Document.RelativePath, r.Version.BlobPath, r.Version.BlobUrl,
                r.Version.SizeBytes, r.Version.ContentType, r.Version.ContentSha256, r.Version.Revision,
                r.Version.ContentMd5, r.Version.Id, r.Document.SourceType.ToString(), r.Version.ExternalId)).ToList();
            var prefix = group.First().Version.BlobPath[..group.First().Version.BlobPath.LastIndexOf('/')];
            var origin = await db.SourceDocuments.AsNoTracking().FirstAsync(s => s.Id == group.Key, ct);
            var message = new DocumentsProcessRequestedEvent(
                origin.Id, origin.Reference, source.ArtifactId, organizationId, source.Artifact.Name, team.Name,
                source.Artifact.Group.Name, origin.SourceType.ToString(), blobs.ContainerName, prefix,
                documents.Count, DateTimeOffset.UtcNow, documents);
            await queues.SendAsync(serviceBus.Value.ProcessQueue, $"restore-{Guid.NewGuid():n}",
                DochubEvents.DocumentsProcessRequested, DochubEvents.Serialize(message), source.ArtifactId.ToString(), ct);
        }

        var restoredCount = touched.Count - removed;
        await notifications.RaiseAsync(organizationId, null, "upload.removed", "Upload removed",
            $"{source.Reference} was removed from {source.Artifact.Name}: {removed} document(s) deleted" +
            (restoredCount > 0 ? $", {restoredCount} restored to their previous version" : "") + ".",
            NotificationSeverity.Info, "artifact", source.ArtifactId, ct);
        log.LogInformation("{Reference} removed: {Removed} deleted, {Restored} restored, {Blobs} blobs, {Reindex} re-queued",
            source.Reference, removed, touched.Count - removed, blobsDeleted, reindex.Count);

        return new RemovalResult(source.Reference, removed, touched.Count - removed, blobsDeleted, reindex.Count);
    }
}
