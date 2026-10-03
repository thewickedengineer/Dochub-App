using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Tests;

/// <summary>
/// A failed upload takes its files back out of Blob storage by itself, and the user
/// can remove a failed or partly failed upload: index, files and rows, with documents
/// it only updated going back to their previous version.
/// </summary>
public class UploadRemovalTests : PipelineTestBase
{
    private async Task<Guid> ScheduleAsync()
    {
        var schedule = new RecurringSyncSchedule
        {
            OrganizationId = OrganizationId, ArtifactId = Artifact.Id, SourceType = SourceType.GoogleDrive,
            SourceConnectionId = Connection.Id, SourceReference = "Drive › Policy",
            SourceOptions = System.Text.Json.JsonDocument.Parse("""{"folderId":"root"}"""),
            Frequency = SyncFrequency.Daily, TimeOfDay = new TimeOnly(9, 0), TimeZoneId = "UTC",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(1), CreatedByUserId = UserId
        };
        Db.RecurringSyncSchedules.Add(schedule);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return schedule.Id;
    }

    /// <summary>Runs the extractor and, like the worker, tolerates the rethrow on failure.</summary>
    private async Task<SourceDocument> SubmitTolerantAsync(Guid? scheduleId = null)
    {
        try { return await SubmitAndExtractAsync(scheduleId); }
        catch (HttpRequestException)
        {
            Db.ChangeTracker.Clear();
            return await Db.SourceDocuments.Include(x => x.Documents).OrderByDescending(x => x.RequestedAt).FirstAsync();
        }
    }

    private async Task MarkFailedAsync(Guid sourceId)
    {
        await Db.SourceDocuments.Where(x => x.Id == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SourceDocumentStatus.PartiallyFailed));
        Db.ChangeTracker.Clear();
    }

    // ── automatic: a failed upload takes its files back ──────────────────────

    [Fact]
    public async Task An_upload_that_fails_midway_deletes_the_files_it_already_stored()
    {
        Extractor.Files = new() { ["a.docx"] = "a", ["b.docx"] = "b", ["c.docx"] = "c" };
        Extractor.ThrowAfter = 2;

        var source = await SubmitTolerantAsync();

        Assert.Equal(SourceDocumentStatus.Failed, source.Status);
        Assert.Empty(Blobs.Blobs);                                    // a and b were stored, then taken back
        Assert.Equal(2, Blobs.Deleted.Count);
        Assert.Empty(await Db.DocumentVersions.ToListAsync());
        Assert.All(source.Documents, d =>
        {
            Assert.Equal(DocumentStatus.Failed, d.Status);
            Assert.Null(d.BlobPath);
        });
        Assert.Empty(Queues.On(ProcessQueue));                         // nothing was handed on
    }

    [Fact]
    public async Task A_failed_sync_puts_updated_documents_back_on_their_previous_version()
    {
        var scheduleId = await ScheduleAsync();
        Extractor.Files = new() { ["rates.xlsx"] = "v1", ["terms.docx"] = "t1" };
        var first = await SubmitTolerantAsync(scheduleId);
        await MarkProcessedAsync(first.Id);
        var before = await Db.UploadedDocuments.AsNoTracking().SingleAsync(d => d.Name == "rates.xlsx");

        Extractor.Files = new() { ["rates.xlsx"] = "v2", ["terms.docx"] = "t2" };
        Extractor.ThrowAfter = 1;
        await SubmitTolerantAsync(scheduleId);

        var after = await Db.UploadedDocuments.AsNoTracking().SingleAsync(d => d.Name == "rates.xlsx");
        Assert.Equal(before.ContentMd5, after.ContentMd5);
        Assert.Equal(before.BlobPath, after.BlobPath);
        Assert.Equal(1, after.Revision);
        Assert.Equal(DocumentStatus.Indexed, after.Status);
        Assert.True(Blobs.Blobs.ContainsKey(before.BlobPath!));      // the earlier file is untouched
    }

    // ── Remove ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Removing_an_upload_clears_the_index_its_files_and_every_row()
    {
        Extractor.Files = new() { ["a.docx"] = "a", ["b.docx"] = "b" };
        var source = await SubmitTolerantAsync();
        await MarkFailedAsync(source.Id);
        var ids = source.Documents.Select(d => d.Id).ToHashSet();

        var result = await Removal.RemoveAsync(source.Id, OrganizationId, default);
        Db.ChangeTracker.Clear();

        Assert.Equal(2, result.DocumentsRemoved);
        Assert.Equal(ids, Rag.Calls.Single().Select(c => c.DocumentId).ToHashSet());
        Assert.All(Rag.Calls.Single(), c => Assert.Null(c.KeepVersionId));
        Assert.Empty(Blobs.Blobs);
        Assert.False(await Db.SourceDocuments.AnyAsync(s => s.Id == source.Id));
        Assert.False(await Db.UploadedDocuments.AnyAsync(d => ids.Contains(d.Id)));
        Assert.Empty(await Db.DocumentVersions.ToListAsync());
        Assert.False(await Db.Notifications.AnyAsync(n => n.EntityId == source.Id && n.Type != "upload.removed"));
        Assert.Equal(ArtifactStatus.Empty, (await Db.Artifacts.SingleAsync()).Status);
    }

    [Fact]
    public async Task Removing_a_sync_restores_what_it_updated_and_reindexes_only_if_needed()
    {
        var scheduleId = await ScheduleAsync();
        Extractor.Files = new() { ["rates.xlsx"] = "v1" };
        var first = await SubmitTolerantAsync(scheduleId);
        await MarkProcessedAsync(first.Id);
        var original = await Db.UploadedDocuments.AsNoTracking().SingleAsync();

        Extractor.Files = new() { ["rates.xlsx"] = "v2", ["new.docx"] = "n" };
        var second = await SubmitTolerantAsync(scheduleId);
        await MarkFailedAsync(second.Id);

        // The index already holds v2 of rates.xlsx, so the restored v1 must be indexed again.
        Rag.WhenKeepRequested = "removed";
        Queues.Messages.Clear();
        var result = await Removal.RemoveAsync(second.Id, OrganizationId, default);
        Db.ChangeTracker.Clear();

        Assert.Equal((1, 1, 1), (result.DocumentsRemoved, result.DocumentsRestored, result.Reindexed));
        var restored = await Db.UploadedDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.ContentMd5, restored.ContentMd5);
        Assert.Equal(first.Id, restored.SourceDocumentId);
        Assert.Equal(DocumentStatus.Uploaded, restored.Status);
        var requeued = DochubEvents.Deserialize<DocumentsProcessRequestedEvent>(Queues.On(ProcessQueue).Single().Payload);
        Assert.Equal(first.Id, requeued.SourceDocumentId);
        Assert.Equal(restored.CurrentVersionId, requeued.Documents.Single().DocumentVersionId);
        Assert.True(Blobs.Blobs.ContainsKey(original.BlobPath!));
    }

    [Fact]
    public async Task If_the_index_cannot_be_reached_nothing_is_deleted_and_remove_can_be_retried()
    {
        Extractor.Files = new() { ["a.docx"] = "a" };
        var source = await SubmitTolerantAsync();
        await MarkFailedAsync(source.Id);
        Rag.Unavailable = true;

        var error = await Assert.ThrowsAsync<RemovalBlockedException>(() => Removal.RemoveAsync(source.Id, OrganizationId, default));
        Db.ChangeTracker.Clear();
        Assert.Equal("rag_unavailable", error.Code);
        var paused = await Db.SourceDocuments.Include(s => s.Documents).SingleAsync(s => s.Id == source.Id);
        Assert.Equal(SourceDocumentStatus.Removing, paused.Status);
        Assert.Single(paused.Documents);
        Assert.Single(Blobs.Blobs);

        Rag.Unavailable = false;
        await Removal.RemoveAsync(source.Id, OrganizationId, default);
        Db.ChangeTracker.Clear();
        Assert.False(await Db.SourceDocuments.AnyAsync(s => s.Id == source.Id));
        Assert.Empty(Blobs.Blobs);
    }

    [Fact]
    public async Task Successful_or_running_uploads_cannot_be_removed_and_queued_work_is_withdrawn()
    {
        Extractor.Files = new() { ["a.docx"] = "a" };
        var source = await SubmitTolerantAsync();
        await MarkProcessedAsync(source.Id);
        var blocked = await Assert.ThrowsAsync<RemovalBlockedException>(() => Removal.RemoveAsync(source.Id, OrganizationId, default));
        Assert.Equal("not_removable", blocked.Code);

        await MarkFailedAsync(source.Id);
        Db.QueueMessages.Add(new QueueMessage
        {
            Queue = ProcessQueue, MessageId = "retry-1", Subject = "x", Status = QueueMessageStatus.InFlight,
            Payload = $$"""{"sourceDocumentId":"{{source.Id}}"}"""
        });
        await Db.SaveChangesAsync();
        var busy = await Assert.ThrowsAsync<RemovalBlockedException>(() => Removal.RemoveAsync(source.Id, OrganizationId, default));
        Assert.Equal("in_progress", busy.Code);

        await Db.QueueMessages.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, QueueMessageStatus.Pending));
        await Removal.RemoveAsync(source.Id, OrganizationId, default);
        Assert.False(await Db.QueueMessages.AnyAsync());
    }
}
