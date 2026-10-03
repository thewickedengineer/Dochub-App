using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Tests;

/// <summary>
/// Every blob written leaves a version row carrying the hash storage returned,
/// and a recurring update decides "changed or not" from that hash.
/// </summary>
public class DocumentVersionTests : PipelineTestBase
{
    [Fact]
    public async Task Each_upload_records_a_version_with_the_hash_storage_returned()
    {
        Extractor.Files = new() { ["Policy.docx"] = "policy text" };
        var source = await SubmitAndExtractAsync();

        var document = Assert.Single(source.Documents);
        var version = await Db.DocumentVersions.AsNoTracking().SingleAsync(x => x.UploadedDocumentId == document.Id);

        var expectedMd5 = Convert.ToBase64String(
            System.Security.Cryptography.MD5.HashData("policy text"u8.ToArray()));

        Assert.Equal(expectedMd5, version.ContentMd5);
        Assert.Equal(expectedMd5, document.ContentMd5);
        Assert.Equal(version.Id, document.CurrentVersionId);
        Assert.Equal("Policy.docx", version.Name);
        Assert.Equal(document.BlobPath, version.BlobPath);
        Assert.False(string.IsNullOrWhiteSpace(version.BlobETag));
        Assert.Equal(64, version.ContentSha256.Length);
    }

    [Fact]
    public async Task The_process_message_carries_the_hash_and_version()
    {
        Extractor.Files = new() { ["Policy.docx"] = "policy text" };
        await SubmitAndExtractAsync();

        var message = DochubEvents.Deserialize<DocumentsProcessRequestedEvent>(
            Queues.On(ProcessQueue).Single().Payload);
        var document = Assert.Single(message.Documents);
        var version = await Db.DocumentVersions.AsNoTracking().SingleAsync();

        Assert.Equal(version.ContentMd5, document.ContentMd5);
        Assert.Equal(version.Id, document.DocumentVersionId);
    }

    [Fact]
    public async Task A_changed_file_adds_a_second_version_and_keeps_the_first()
    {
        var scheduleId = await ScheduleAsync();

        Extractor.Files = new() { ["Rates.xlsx"] = "v1" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);

        Extractor.Files = new() { ["Rates.xlsx"] = "v2" };
        await SubmitAndExtractAsync(scheduleId);

        var versions = await Db.DocumentVersions.AsNoTracking().OrderBy(x => x.Revision).ToListAsync();
        Assert.Equal(2, versions.Count);
        Assert.Equal([1, 2], versions.Select(v => v.Revision));
        Assert.NotEqual(versions[0].ContentMd5, versions[1].ContentMd5);

        var document = await Db.UploadedDocuments.AsNoTracking().SingleAsync();
        Assert.Equal(versions[1].Id, document.CurrentVersionId);
    }

    [Fact]
    public async Task Unchanged_bytes_add_no_version()
    {
        var scheduleId = await ScheduleAsync();

        Extractor.Files = new() { ["Rates.xlsx"] = "same" };
        var first = await SubmitAndExtractAsync(scheduleId);
        await MarkProcessedAsync(first.Id);

        var second = await SubmitAndExtractAsync(scheduleId);

        Assert.Equal(1, second.UnchangedDocumentCount);
        Assert.Equal(1, await Db.DocumentVersions.CountAsync());
    }

    [Theory]
    [InlineData(new DocumentStatus[0], ArtifactStatus.Empty)]
    [InlineData(new[] { DocumentStatus.Indexed, DocumentStatus.Indexed }, ArtifactStatus.Processed)]
    [InlineData(new[] { DocumentStatus.Indexed, DocumentStatus.Processing }, ArtifactStatus.Processing)]
    [InlineData(new[] { DocumentStatus.Indexed, DocumentStatus.Uploaded }, ArtifactStatus.Processing)]
    [InlineData(new[] { DocumentStatus.Indexed, DocumentStatus.Failed }, ArtifactStatus.PartiallyProcessed)]
    [InlineData(new[] { DocumentStatus.Failed, DocumentStatus.Failed }, ArtifactStatus.Failed)]
    public void Artifact_status_reflects_every_document_not_just_one_batch(
        DocumentStatus[] states, ArtifactStatus expected) =>
        Assert.Equal(expected, IngestionEndpoints.ArtifactStatusFrom(states));

    private async Task<Guid> ScheduleAsync()
    {
        var schedule = new RecurringSyncSchedule
        {
            OrganizationId = OrganizationId,
            ArtifactId = Artifact.Id,
            SourceType = SourceType.GoogleDrive,
            SourceConnectionId = Connection.Id,
            SourceReference = "Drive › Rates",
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
}
