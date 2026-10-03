using System.Text.Json;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

/// <summary>
/// Handles one source taken off the extract queue: moves its files into blob
/// storage, records each one, and hands the batch to the process queue.
///
///   status RequestUpload -> Uploading   (claimed, bytes moving)
///                        -> Uploaded    (all files stored, process queue has it)
/// </summary>
public interface IExtractorService
{
    Task HandleAsync(SourceUploadRequestedEvent request, CancellationToken ct);
}

public class ExtractorService(
    DochubDbContext db,
    IBlobStorageService blobs,
    ISourceExtractorFactory extractors,
    IStagingStore staging,
    ISourceTokenProvider tokens,
    INotificationService notifications,
    IQueueClient queues,
    IOptions<ServiceBusOptions> serviceBus,
    ILogger<ExtractorService> log,
    UploadRemovalService removal) : IExtractorService
{
    public async Task HandleAsync(SourceUploadRequestedEvent request, CancellationToken ct)
    {
        var source = await db.SourceDocuments
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == request.SourceDocumentId, ct);

        if (source is null)
        {
            log.LogWarning("Source {Id} no longer exists; dropping the message", request.SourceDocumentId);
            return;
        }

        if (source.Status is SourceDocumentStatus.Uploaded or SourceDocumentStatus.Processing
            or SourceDocumentStatus.Processed or SourceDocumentStatus.Cancelled)
        {
            // A redelivery after the work already landed. Completing the message
            // is correct here — repeating the upload would duplicate blobs.
            log.LogInformation("Source {Reference} is already {Status}; nothing to do", source.Reference, source.Status);
            return;
        }

        var artifact = source.Artifact;
        var group = artifact.Group;
        var team = group.Team;

        // ── Status: Uploading ──────────────────────────────────────────────────
        source.Status = SourceDocumentStatus.Uploading;
        source.UploadStartedAt ??= DateTimeOffset.UtcNow;
        source.DeliveryCount++;
        source.Error = null;
        await db.SaveChangesAsync(ct);

        var prefix = source.BlobPrefix ?? blobs.BuildPrefix(team.Slug, group.Slug, artifact.Slug, source.RequestedAt);
        source.BlobContainer = blobs.ContainerName;
        source.BlobPrefix = prefix;

        var stored = new List<ProcessableDocument>();
        var failures = 0;
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        // Once the documents are on the process queue their files must stay, whatever fails after.
        var sentForProcessing = false;
        // What the source listed this time, keyed the same way as the reconciliation.
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var removedIds = new List<Guid>();

        // A scheduled run reconciles against what the artifact already holds,
        // keyed by file name; a one-off import has nothing to reconcile against.
        // A repository has many files with the same name (README.md, index.ts), so
        // GitHub is keyed by the path within the repository instead.
        var reconciling = source.SyncScheduleId is not null;
        Func<string, string, string> keyOf = source.SourceType == SourceType.GitHub
            ? (_, relativePath) => relativePath
            : (name, _) => name;
        var byName = reconciling
            ? (await db.UploadedDocuments
                .Where(x => x.ArtifactId == source.ArtifactId && x.SourceType == source.SourceType)
                .ToListAsync(ct))
                .GroupBy(x => keyOf(x.Name, x.RelativePath), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase)
            : [];

        try
        {
            var (token, connection) = await ResolveTokenAsync(source, ct);
            var options = source.SourceOptions?.RootElement ?? JsonDocument.Parse("{}").RootElement;
            var extractor = extractors.Get(source.SourceType);
            var context = new ExtractionContext(source.SourceType, source.SourceReference, options, token);

            await foreach (var extracted in AuthenticatedExtraction.Guarded(
                extractor.ExtractAsync(context, ct), connection, t => db.SaveChangesAsync(t), ct))
            {
                ct.ThrowIfCancellationRequested();
                listed.Add(keyOf(extracted.Name, extracted.RelativePath));
                var match = reconciling ? byName.GetValueOrDefault(keyOf(extracted.Name, extracted.RelativePath)) : null;
                var document = match ?? await MatchOrCreateAsync(source, extracted, ct);
                BlobUploadResult? written = null;
                // Only content that actually made it into the index can be skipped as unchanged.
                var wasIndexed = match?.Status == DocumentStatus.Indexed;

                try
                {
                    document.Status = DocumentStatus.Uploading;
                    document.AttemptCount++;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    var blobPath = $"{prefix}/{extracted.RelativePath.TrimStart('/')}";
                    await using var content = await extracted.OpenAsync(ct);
                    var result = await blobs.UploadAsync(blobPath, content, extracted.ContentType, ct);
                    written = result;

                    if (match is not null && wasIndexed
                        && match.ContentMd5 is not null
                        && string.Equals(match.ContentMd5, result.ContentMd5, StringComparison.Ordinal))
                    {
                        // Byte-identical to what is already indexed: leave the
                        // existing vectors alone and drop the blob just written.
                        match.Status = DocumentStatus.Indexed;
                        match.LastSyncedAt = source.RequestedAt;
                        match.UpdatedAt = DateTimeOffset.UtcNow;
                        await db.SaveChangesAsync(ct);
                        await blobs.DeleteAsync(result.BlobPath, ct);
                        unchanged++;
                        continue;
                    }

                    if (match is not null)
                    {
                        // Same file name, new content — the same document record is
                        // re-pointed, so its vectors are replaced, not duplicated.
                        match.SourceDocumentId = source.Id;
                        match.ExternalId = extracted.ExternalId ?? match.ExternalId;
                        match.RelativePath = extracted.RelativePath;
                        match.SourceLocation = extracted.SourceLocation;
                        match.Revision++;
                        match.LastSyncedAt = source.RequestedAt;
                        updated++;
                    }
                    else if (reconciling)
                    {
                        document.LastSyncedAt = source.RequestedAt;
                        added++;
                    }

                    // The version row is the record of what was actually stored,
                    // and the history a later sync compares against.
                    var version = new DocumentVersion
                    {
                        UploadedDocumentId = document.Id,
                        SourceDocumentId = source.Id,
                        ArtifactId = source.ArtifactId,
                        Revision = document.Revision,
                        Name = document.Name,
                        RelativePath = extracted.RelativePath,
                        SourceLocation = extracted.SourceLocation,
                        SourceType = source.SourceType,
                        ExternalId = extracted.ExternalId ?? document.ExternalId,
                        ContentType = extracted.ContentType ?? document.ContentType,
                        SizeBytes = result.SizeBytes,
                        BlobContainer = blobs.ContainerName,
                        BlobPath = result.BlobPath,
                        BlobUrl = result.BlobUrl,
                        BlobETag = result.ETag,
                        ContentMd5 = result.ContentMd5,
                        ContentSha256 = result.ChecksumSha256,
                        BlobLastModified = result.LastModified
                    };
                    db.DocumentVersions.Add(version);

                    document.Status = DocumentStatus.Uploaded;
                    document.BlobPath = result.BlobPath;
                    document.BlobUrl = result.BlobUrl;
                    document.SizeBytes = result.SizeBytes;
                    document.ChecksumSha256 = result.ChecksumSha256;
                    document.ContentMd5 = result.ContentMd5;
                    document.BlobETag = result.ETag;
                    document.CurrentVersionId = version.Id;
                    document.ContentType = extracted.ContentType ?? document.ContentType;
                    document.BlobUploadedAt = DateTimeOffset.UtcNow;
                    document.ProcessedAt = null;
                    document.ChunkCount = null;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    document.Error = null;
                    await db.SaveChangesAsync(ct);

                    stored.Add(new ProcessableDocument(document.Id, document.Name, document.RelativePath,
                        result.BlobPath, result.BlobUrl, result.SizeBytes, document.ContentType,
                        result.ChecksumSha256, document.Revision, result.ContentMd5, version.Id,
                        document.SourceType.ToString(), extracted.ExternalId ?? document.ExternalId));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The file reached Blob storage but recording it failed: don't leave it orphaned.
                    if (written is not null)
                    {
                        try { await blobs.DeleteAsync(written.BlobPath, ct); }
                        catch (Exception cleanup) { log.LogWarning(cleanup, "Could not delete orphaned blob {Path}", written.BlobPath); }
                    }
                    if (ex is DbUpdateException rejected) await ForgetRejectedAsync(rejected, source, ct);
                    failures++;
                    document.Status = DocumentStatus.Failed;
                    document.Error = Truncate(ex.Message);
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    log.LogError(ex, "Upload failed for {Name} in {Reference}", document.Name, source.Reference);
                }
            }

            // Files deleted at the source go from Dochub and the index too — only after
            // the listing finished, so a listing that broke off never reads as deletions.
            if (reconciling)
                removedIds = await RemoveDeletedAtSourceAsync(source, listed, keyOf, ct);

            // Rows declared up front that the source never produced are not failures.
            foreach (var orphan in source.Documents.Where(d => d.Status == DocumentStatus.Pending))
            {
                orphan.Status = DocumentStatus.Skipped;
                orphan.Error = "Not present in the source at extraction time.";
                orphan.UpdatedAt = DateTimeOffset.UtcNow;
            }

            source.TotalDocuments = stored.Count + failures;
            source.UploadedDocumentCount = stored.Count;
            source.FailedDocumentCount = failures;
            source.AddedDocumentCount = added;
            source.UpdatedDocumentCount = updated;
            source.UnchangedDocumentCount = unchanged;
            source.RemovedDocumentCount = removedIds.Count;
            source.UploadedAt = DateTimeOffset.UtcNow;

            if (stored.Count == 0 && removedIds.Count == 0)
            {
                // A scheduled run that finds nothing changed is a success, not a
                // failure — there is simply no vector work to do.
                if (reconciling && failures == 0)
                {
                    source.Status = SourceDocumentStatus.Processed;
                    source.ProcessedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    await RecordScheduleTallyAsync(source, ct);
                    log.LogInformation("{Reference}: nothing changed ({Unchanged} document(s) already current)",
                        source.Reference, unchanged);
                    return;
                }

                source.Status = SourceDocumentStatus.Failed;
                source.Error = failures > 0
                    ? $"All {failures} document(s) failed to upload."
                    : "The source returned no documents.";
                await db.SaveChangesAsync(ct);
                await NotifyAsync(source, team.OrganizationId, NotificationSeverity.Error,
                    "Upload failed", $"{source.Reference} · {source.Error}", ct);
                return;
            }

            source.Status = failures > 0 ? SourceDocumentStatus.PartiallyFailed : SourceDocumentStatus.Uploaded;
            if (removedIds.Count > 0)
                log.LogInformation("{Reference}: {Count} document(s) were deleted at the source", source.Reference, removedIds.Count);
            await db.SaveChangesAsync(ct);

            // ── Queue 2: hand the uploaded documents to the vector service ─────
            var processEvent = new DocumentsProcessRequestedEvent(
                source.Id, source.Reference, artifact.Id, team.OrganizationId, artifact.Name,
                team.Name, group.Name, source.SourceType.ToString(), blobs.ContainerName, prefix,
                stored.Count, source.UploadedAt.Value, stored, removedIds);

            artifact.Status = ArtifactStatus.Processing;
            await db.SaveChangesAsync(ct);

            await queues.SendAsync(serviceBus.Value.ProcessQueue,
                $"process-{source.Id:n}", DochubEvents.DocumentsProcessRequested,
                DochubEvents.Serialize(processEvent), artifact.Id.ToString(), ct);
            sentForProcessing = true;

            await RecordScheduleTallyAsync(source, ct);

            await NotifyAsync(source, team.OrganizationId,
                failures == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning,
                failures == 0 ? "Documents uploaded" : "Upload finished with errors",
                (failures == 0
                    ? $"{source.Reference} · {stored.Count} document{(stored.Count == 1 ? "" : "s")} from {source.SourceReference} are queued for processing."
                    : $"{source.Reference} · {stored.Count} uploaded, {failures} failed for {artifact.Name}.")
                + (removedIds.Count > 0 ? $" {removedIds.Count} deleted at the source were removed." : ""),
                ct);

            log.LogInformation("{Reference}: {Stored} uploaded, {Failed} failed → {Queue}",
                source.Reference, stored.Count, failures, serviceBus.Value.ProcessQueue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A rejected save leaves its rows in the change tracker, and every later
            // save would hit the same error — let go of them before recording the failure.
            if (ex is DbUpdateException rejected) await ForgetRejectedAsync(rejected, source, ct);

            source.Status = SourceDocumentStatus.Failed;
            source.Error = Truncate(ex.Message);
            source.UploadedAt = DateTimeOffset.UtcNow;
            foreach (var document in source.Documents.Where(d => d.Status is DocumentStatus.Pending or DocumentStatus.Uploading))
            {
                document.Status = DocumentStatus.Failed;
                document.Error = Truncate(ex.Message);
                document.UpdatedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);

            // The upload failed before anything was handed on: take back every file it
            // stored. A retry of the message starts again from nothing.
            if (!sentForProcessing)
            {
                try
                {
                    await removal.RollBackStoredFilesAsync(source,
                        "Rolled back: the upload failed, so its files were removed from storage.", ct);
                }
                catch (Exception cleanup) when (cleanup is not OperationCanceledException)
                {
                    log.LogError(cleanup, "Could not roll back the files of {Reference}", source.Reference);
                }
            }

            await NotifyAsync(source, team.OrganizationId, NotificationSeverity.Error, "Upload failed",
                $"{source.Reference} · {source.SourceReference} could not be imported into {artifact.Name}: {Truncate(ex.Message, 200)}", ct);

            log.LogError(ex, "Source {Reference} failed", source.Reference);
            throw;   // let the worker decide whether to retry the message
        }
        finally
        {
            await CleanStagingAsync(source, ct);
        }
    }

    /// <summary>
    /// A scheduled run's view of deletions: documents this location brought in that
    /// it no longer lists. Scoped to the same location (source type and reference),
    /// so a sync never removes files that arrived by another import. A listing with
    /// nothing in it is treated as suspect — an emptied or unreadable folder — and
    /// removes nothing.
    /// </summary>
    private async Task<List<Guid>> RemoveDeletedAtSourceAsync(
        SourceDocument source, HashSet<string> listed, Func<string, string, string> keyOf, CancellationToken ct)
    {
        if (listed.Count == 0)
        {
            log.LogWarning("{Reference}: the source listed nothing; not treating that as deletions", source.Reference);
            return [];
        }

        var fromThisLocation = await db.UploadedDocuments
            .Where(d => d.ArtifactId == source.ArtifactId && d.SourceType == source.SourceType
                        && d.SourceDocument.SourceReference == source.SourceReference
                        && d.Status != DocumentStatus.Skipped)
            .ToListAsync(ct);
        var gone = fromThisLocation.Where(d => !listed.Contains(keyOf(d.Name, d.RelativePath))).ToList();
        if (gone.Count == 0) return [];

        var ids = gone.Select(d => d.Id).ToList();
        // Every stored version's file goes with the document.
        var paths = await db.DocumentVersions.Where(v => ids.Contains(v.UploadedDocumentId)).Select(v => v.BlobPath).ToListAsync(ct);
        db.UploadedDocuments.RemoveRange(gone);   // versions follow (cascade)
        await db.SaveChangesAsync(ct);
        foreach (var path in paths.Distinct())
        {
            try { await blobs.DeleteAsync(path, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Could not delete {Path} of a document removed at the source", path);
            }
        }
        return ids;
    }

    /// <summary>
    /// Reuses the row the endpoint declared for a local file, and otherwise adds
    /// one as the connector discovers it.
    /// </summary>
    private async Task<UploadedDocument> MatchOrCreateAsync(SourceDocument source, ExtractedDocument extracted, CancellationToken ct)
    {
        var existing = source.Documents.FirstOrDefault(d =>
            d.Status == DocumentStatus.Pending &&
            string.Equals(d.RelativePath, extracted.RelativePath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.ExternalId ??= extracted.ExternalId;
            existing.ContentType ??= extracted.ContentType;
            return existing;
        }

        // The artifact already holds this source item from an earlier import: carry
        // that record over to this upload rather than adding a second copy of it.
        if (extracted.ExternalId is not null)
        {
            var previous = await db.UploadedDocuments.FirstOrDefaultAsync(d =>
                d.ArtifactId == source.ArtifactId && d.SourceType == source.SourceType &&
                d.ExternalId == extracted.ExternalId, ct);
            if (previous is not null)
            {
                previous.SourceDocumentId = source.Id;
                previous.Name = extracted.Name;
                previous.RelativePath = extracted.RelativePath;
                previous.SourceLocation = extracted.SourceLocation;
                previous.ContentType = extracted.ContentType ?? previous.ContentType;
                if (previous.CurrentVersionId is not null) previous.Revision++;
                previous.Status = DocumentStatus.Pending;
                previous.Error = null;
                previous.UpdatedAt = DateTimeOffset.UtcNow;
                if (!source.Documents.Contains(previous)) source.Documents.Add(previous);
                await db.SaveChangesAsync(ct);
                return previous;
            }
        }

        var document = new UploadedDocument
        {
            SourceDocumentId = source.Id,
            ArtifactId = source.ArtifactId,
            SourceType = source.SourceType,
            ExternalId = extracted.ExternalId,
            Name = extracted.Name,
            RelativePath = extracted.RelativePath,
            SourceLocation = extracted.SourceLocation,
            ContentType = extracted.ContentType,
            SizeBytes = extracted.SizeHint,
            Status = DocumentStatus.Pending
        };
        source.Documents.Add(document);
        db.UploadedDocuments.Add(document);
        await db.SaveChangesAsync(ct);
        return document;
    }

    private async Task ForgetRejectedAsync(DbUpdateException rejected, SourceDocument source, CancellationToken ct)
    {
        foreach (var entry in rejected.Entries)
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity is UploadedDocument document) source.Documents.Remove(document);
                entry.State = EntityState.Detached;
            }
            else
            {
                await entry.ReloadAsync(ct);
            }
        }
    }

    private async Task<(string? Token, SourceConnection? Connection)> ResolveTokenAsync(SourceDocument source, CancellationToken ct)
    {
        if (source.SourceConnectionId is null) return (null, null);
        var connection = await db.SourceConnections.FirstOrDefaultAsync(x => x.Id == source.SourceConnectionId, ct);
        if (connection is null) return (null, null);

        // Renews from the stored refresh token when the access token is stale, so
        // a schedule set up weeks ago still works without anyone signing in again.
        return (await tokens.GetAccessTokenAsync(connection, ct), connection);
    }

    /// <summary>Copies a scheduled run's tallies onto the schedule for the UI.</summary>
    private async Task RecordScheduleTallyAsync(SourceDocument source, CancellationToken ct)
    {
        if (source.SyncScheduleId is not { } scheduleId) return;
        var schedule = await db.RecurringSyncSchedules.FirstOrDefaultAsync(x => x.Id == scheduleId, ct);
        if (schedule is null) return;

        schedule.DocumentsAddedLastRun = source.AddedDocumentCount;
        schedule.DocumentsUpdatedLastRun = source.UpdatedDocumentCount;
        schedule.DocumentsUnchangedLastRun = source.UnchangedDocumentCount;
        schedule.LastSourceDocumentId = source.Id;
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task CleanStagingAsync(SourceDocument source, CancellationToken ct)
    {
        if (source.SourceType != SourceType.Local || source.SourceOptions is null) return;
        if (!source.SourceOptions.RootElement.TryGetProperty("stagingIds", out var ids) || ids.ValueKind != JsonValueKind.Array) return;
        foreach (var id in ids.EnumerateArray())
            if (id.GetString() is { } value) await staging.DeleteAsync(value, ct);
    }

    private Task NotifyAsync(SourceDocument source, Guid organizationId, NotificationSeverity severity,
        string title, string body, CancellationToken ct) =>
        notifications.RaiseAsync(organizationId, null,
            severity == NotificationSeverity.Error ? "upload.failed" : "upload.completed",
            title, body, severity, "sourceDocument", source.Id, ct);

    private static string Truncate(string value, int max = 2000) => value.Length <= max ? value : value[..max];
}
