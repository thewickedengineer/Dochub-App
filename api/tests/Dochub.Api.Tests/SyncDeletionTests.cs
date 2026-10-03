using System.Text.Json;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Tests;

/// <summary>A file deleted at the source goes from Dochub, Blob storage and (via the queue) the index.</summary>
public class SyncDeletionTests : PipelineTestBase
{
    private async Task<Guid> ScheduleAsync()
    {
        var schedule = new RecurringSyncSchedule
        {
            OrganizationId = OrganizationId, ArtifactId = Artifact.Id, SourceType = SourceType.GoogleDrive,
            SourceConnectionId = Connection.Id, SourceReference = "Drive › Policy",
            SourceOptions = JsonDocument.Parse("""{"folderId":"root"}"""),
            Frequency = SyncFrequency.Daily, TimeOfDay = new TimeOnly(9, 0), TimeZoneId = "UTC",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(1), CreatedByUserId = UserId
        };
        Db.RecurringSyncSchedules.Add(schedule);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return schedule.Id;
    }

    private DocumentsProcessRequestedEvent LastProcessMessage() =>
        DochubEvents.Deserialize<DocumentsProcessRequestedEvent>(Queues.On(ProcessQueue).Last().Payload);

    [Fact]
    public async Task A_file_gone_from_the_source_is_removed_and_the_index_is_told()
    {
        var scheduleId = await ScheduleAsync();
        Extractor.Files = new() { ["keep.docx"] = "k", ["drop.docx"] = "d" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);
        var dropped = await Db.UploadedDocuments.AsNoTracking().SingleAsync(d => d.Name == "drop.docx");

        Extractor.Files = new() { ["keep.docx"] = "k" };
        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(1, second.RemovedDocumentCount);
        Assert.Equal(1, second.UnchangedDocumentCount);
        Assert.False(await Db.UploadedDocuments.AnyAsync(d => d.Id == dropped.Id));
        Assert.False(await Db.DocumentVersions.AnyAsync(v => v.UploadedDocumentId == dropped.Id));
        Assert.False(Blobs.Blobs.ContainsKey(dropped.BlobPath!));
        // Nothing to index, but the deletion still goes to the vector service.
        var message = LastProcessMessage();
        Assert.Equal(second.Id, message.SourceDocumentId);
        Assert.Empty(message.Documents);
        Assert.Equal([dropped.Id], message.RemovedDocumentIds!);
        Assert.Equal(SourceDocumentStatus.Uploaded, second.Status);
    }

    [Fact]
    public async Task An_empty_listing_never_reads_as_everything_deleted()
    {
        var scheduleId = await ScheduleAsync();
        Extractor.Files = new() { ["keep.docx"] = "k" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);

        Extractor.Files = new();
        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(0, second.RemovedDocumentCount);
        Assert.Equal(1, await Db.UploadedDocuments.CountAsync());
    }

    [Fact]
    public async Task Files_from_another_import_into_the_same_artifact_are_left_alone()
    {
        // A one-off import of a different Drive folder into the same artifact.
        var other = await Submissions.SubmitAsync(Artifact, OrganizationId, UserId,
            new SourceSubmission(SourceType.GoogleDrive, "Drive › Other folder", Connection.Id,
                JsonDocument.Parse("""{"folderId":"other"}""").RootElement, []), null, default);
        Extractor.Files = new() { ["elsewhere.docx"] = "e" };
        await Extraction.HandleAsync(DochubEvents.Deserialize<SourceUploadRequestedEvent>(
            Queues.On(ExtractQueue).Last().Payload), default);
        await MarkProcessedAsync(other.Id);

        var scheduleId = await ScheduleAsync();
        Extractor.Files = new() { ["keep.docx"] = "k" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);
        Extractor.Files = new() { ["keep.docx"] = "k", ["new.docx"] = "n" };
        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(0, second.RemovedDocumentCount);
        Assert.True(await Db.UploadedDocuments.AnyAsync(d => d.Name == "elsewhere.docx"));
    }
}
