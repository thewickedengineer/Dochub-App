using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Tests;

/// <summary>
/// A scheduled run goes through the same queues as a manual one, but the
/// extractor reconciles it against the artifact by file name, so a document
/// keeps its identity and only genuinely changed content is re-vectorized.
/// </summary>
public class SyncReconciliationTests : PipelineTestBase
{
    private async Task<Guid> ScheduleAsync()
    {
        var schedule = new RecurringSyncSchedule
        {
            OrganizationId = OrganizationId,
            ArtifactId = Artifact.Id,
            SourceType = SourceType.GoogleDrive,
            SourceConnectionId = Connection.Id,
            SourceReference = "Drive › Policy",
            SourceOptions = System.Text.Json.JsonDocument.Parse("""{"folderId":"root"}"""),
            Frequency = SyncFrequency.Daily,
            TimeOfDay = new TimeOnly(9, 0),
            TimeZoneId = "UTC",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(1),
            CreatedByUserId = UserId
        };
        Db.RecurringSyncSchedules.Add(schedule);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return schedule.Id;
    }

    [Fact]
    public async Task A_changed_file_updates_the_same_document_rather_than_duplicating_it()
    {
        var scheduleId = await ScheduleAsync();

        Extractor.Files = new() { ["HO-3 Homeowners.docx"] = "original text" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);

        var before = await Db.UploadedDocuments.AsNoTracking().SingleAsync(x => x.ArtifactId == Artifact.Id);
        var originalId = before.Id;
        var originalChecksum = before.ChecksumSha256;

        Extractor.Files = new() { ["HO-3 Homeowners.docx"] = "revised text for 2027" };
        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(0, second.AddedDocumentCount);
        Assert.Equal(1, second.UpdatedDocumentCount);

        var document = Assert.Single(await Db.UploadedDocuments.AsNoTracking()
            .Where(x => x.ArtifactId == Artifact.Id).ToListAsync());

        // Identity survives, so anything referencing the document still resolves.
        Assert.Equal(originalId, document.Id);
        Assert.Equal(2, document.Revision);
        Assert.NotEqual(originalChecksum, document.ChecksumSha256);
        Assert.Equal(DocumentStatus.Uploaded, document.Status);

        // And it is handed to the vector service again, so its vectors are replaced.
        var request = DochubEvents.Deserialize<DocumentsProcessRequestedEvent>(
            Queues.On(ProcessQueue).Last().Payload);
        Assert.Equal(originalId, Assert.Single(request.Documents).DocumentId);
    }

    [Fact]
    public async Task An_unchanged_file_costs_no_vector_work()
    {
        var scheduleId = await ScheduleAsync();

        Extractor.Files = new() { ["Personal Auto.pdf"] = "stable text" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);
        var messagesBefore = Queues.On(ProcessQueue).Count();

        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(1, second.UnchangedDocumentCount);
        Assert.Equal(0, second.UpdatedDocumentCount);
        Assert.Equal(SourceDocumentStatus.Processed, second.Status);

        // Nothing new was handed to the vector service.
        Assert.Equal(messagesBefore, Queues.On(ProcessQueue).Count());

        var document = await Db.UploadedDocuments.AsNoTracking().SingleAsync(x => x.ArtifactId == Artifact.Id);
        Assert.Equal(1, document.Revision);
        Assert.Equal(DocumentStatus.Indexed, document.Status);

        // The blob written only to compare checksums is cleaned up.
        Assert.NotEmpty(Blobs.Deleted);
    }

    [Fact]
    public async Task A_mixed_run_tallies_added_updated_and_unchanged_separately()
    {
        var scheduleId = await ScheduleAsync();

        Extractor.Files = new()
        {
            ["HO-3 Homeowners.docx"] = "original text",
            ["Personal Auto.pdf"] = "stable text",
        };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);

        Extractor.Files = new()
        {
            ["HO-3 Homeowners.docx"] = "revised text",        // changed
            ["Personal Auto.pdf"] = "stable text",            // unchanged
            ["Umbrella Endorsement.docx"] = "brand new",      // added
        };
        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(1, second.AddedDocumentCount);
        Assert.Equal(1, second.UpdatedDocumentCount);
        Assert.Equal(1, second.UnchangedDocumentCount);
        Assert.Equal(3, await Db.UploadedDocuments.CountAsync(x => x.ArtifactId == Artifact.Id));

        var schedule = await Db.RecurringSyncSchedules.AsNoTracking().SingleAsync(x => x.Id == scheduleId);
        Assert.Equal(1, schedule.DocumentsAddedLastRun);
        Assert.Equal(1, schedule.DocumentsUpdatedLastRun);
        Assert.Equal(1, schedule.DocumentsUnchangedLastRun);
    }

    [Fact]
    public async Task Matching_ignores_case_so_a_recased_name_is_not_a_new_document()
    {
        var scheduleId = await ScheduleAsync();

        Extractor.Files = new() { ["Reserve Guide.docx"] = "v1" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);

        Extractor.Files = new() { ["reserve guide.DOCX"] = "v2" };
        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(0, second.AddedDocumentCount);
        Assert.Equal(1, second.UpdatedDocumentCount);
        Assert.Equal(1, await Db.UploadedDocuments.CountAsync(x => x.ArtifactId == Artifact.Id));
    }

    [Fact]
    public async Task A_one_off_import_does_not_reconcile_and_keeps_its_own_documents()
    {
        Extractor.Files = new() { ["Doc.txt"] = "v1" };
        var first = await SubmitAndExtractAsync();
        await MarkProcessedAsync(first.Id);

        Extractor.Files = new() { ["Doc.txt"] = "v2" };
        var second = await SubmitAndExtractAsync();

        // No schedule, so no reconciliation: this is a separate import.
        Assert.Equal(0, second.UpdatedDocumentCount);
        Assert.Equal(2, await Db.UploadedDocuments.CountAsync(x => x.ArtifactId == Artifact.Id));
    }
}
