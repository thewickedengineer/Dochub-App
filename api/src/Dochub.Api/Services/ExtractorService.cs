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
    ILogger<ExtractorService> log) : IExtractorService
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

        // A scheduled run reconciles against what the artifact already holds,
        // keyed by file name; a one-off import has nothing to reconcile against.
        var reconciling = source.SyncScheduleId is not null;
        var byName = reconciling
            ? (await db.UploadedDocuments
                .Where(x => x.ArtifactId == source.ArtifactId && x.SourceType == source.SourceType)
                .ToListAsync(ct))
                .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
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
                var match = reconciling ? byName.GetValueOrDefault(extracted.Name) : null;
                var document = match ?? await MatchOrCreateAsync(source, extracted, ct);

                try
                {
                    document.Status = DocumentStatus.Uploading;
                    document.AttemptCount++;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);

                    var blobPath = $"{prefix}/{extracted.RelativePath.TrimStart('/')}";
                    await using var content = await extracted.OpenAsync(ct);
                    var result = await blobs.UploadAsync(blobPath, content, extracted.ContentType, ct);

                    if (match is not null
                        && string.Equals(match.ChecksumSha256, result.ChecksumSha256, StringComparison.OrdinalIgnoreCase))
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

                    document.Status = DocumentStatus.Uploaded;
                    document.BlobPath = result.BlobPath;
                    document.BlobUrl = result.BlobUrl;
                    document.SizeBytes = result.SizeBytes;
                    document.ChecksumSha256 = result.ChecksumSha256;
                    document.ContentType = extracted.ContentType ?? document.ContentType;
                    document.BlobUploadedAt = DateTimeOffset.UtcNow;
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    document.Error = null;
                    await db.SaveChangesAsync(ct);

                    stored.Add(new ProcessableDocument(document.Id, document.Name, document.RelativePath,
                        result.BlobPath, result.BlobUrl, result.SizeBytes, document.ContentType,
                        result.ChecksumSha256, document.Revision));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failures++;
                    document.Status = DocumentStatus.Failed;
                    document.Error = Truncate(ex.Message);
                    document.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                    log.LogError(ex, "Upload failed for {Name} in {Reference}", document.Name, source.Reference);
                }
            }

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
            source.UploadedAt = DateTimeOffset.UtcNow;

            if (stored.Count == 0)
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
            await db.SaveChangesAsync(ct);

            // ── Queue 2: hand the uploaded documents to the vector service ─────
            var processEvent = new DocumentsProcessRequestedEvent(
                source.Id, source.Reference, artifact.Id, team.OrganizationId, artifact.Name,
                team.Name, group.Name, source.SourceType.ToString(), blobs.ContainerName, prefix,
                stored.Count, source.UploadedAt.Value, stored);

            await queues.SendAsync(serviceBus.Value.ProcessQueue,
                $"process-{source.Id:n}", DochubEvents.DocumentsProcessRequested,
                DochubEvents.Serialize(processEvent), artifact.Id.ToString(), ct);

            await RecordScheduleTallyAsync(source, ct);

            await NotifyAsync(source, team.OrganizationId,
                failures == 0 ? NotificationSeverity.Success : NotificationSeverity.Warning,
                failures == 0 ? "Documents uploaded" : "Upload finished with errors",
                failures == 0
                    ? $"{source.Reference} · {stored.Count} document{(stored.Count == 1 ? "" : "s")} from {source.SourceReference} are queued for processing."
                    : $"{source.Reference} · {stored.Count} uploaded, {failures} failed for {artifact.Name}.",
                ct);

            log.LogInformation("{Reference}: {Stored} uploaded, {Failed} failed → {Queue}",
                source.Reference, stored.Count, failures, serviceBus.Value.ProcessQueue);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
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
