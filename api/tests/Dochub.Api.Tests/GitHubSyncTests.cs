using System.Text.Json;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Dochub.Api.Tests;

/// <summary>
/// GitHub repositories can be kept in sync: a public one needs no connection, and
/// files are matched by their path in the repository, because a repository has
/// many files with the same name.
/// </summary>
public class GitHubSyncTests : PipelineTestBase
{
    private async Task<Guid> ScheduleAsync(SourceType type, Guid? connectionId)
    {
        var schedule = new RecurringSyncSchedule
        {
            OrganizationId = OrganizationId, ArtifactId = Artifact.Id, SourceType = type,
            SourceConnectionId = connectionId, SourceReference = "acme/handbook@main:/",
            SourceOptions = JsonDocument.Parse("""{"repository":"acme/handbook","branch":"main"}"""),
            Frequency = SyncFrequency.Daily, TimeOfDay = new TimeOnly(9, 0), TimeZoneId = "UTC",
            NextRunAt = DateTimeOffset.UtcNow.AddDays(1), CreatedByUserId = UserId
        };
        Db.RecurringSyncSchedules.Add(schedule);
        await Db.SaveChangesAsync();
        Db.ChangeTracker.Clear();
        return schedule.Id;
    }

    private async Task<SourceDocument> SyncAsync(Guid scheduleId)
    {
        var sync = new DocumentSyncService(Db, Submissions, NullLogger<DocumentSyncService>.Instance);
        var outcome = await sync.RunAsync(scheduleId, default);
        var message = Queues.On(ExtractQueue).Last();
        await Extraction.HandleAsync(DochubEvents.Deserialize<SourceUploadRequestedEvent>(message.Payload), default);
        Db.ChangeTracker.Clear();
        return await Db.SourceDocuments.Include(x => x.Documents).SingleAsync(x => x.Id == outcome.SourceDocumentId);
    }

    [Fact]
    public async Task A_public_repository_syncs_without_a_connection()
    {
        var scheduleId = await ScheduleAsync(SourceType.GitHub, null);
        Extractor.Files = new() { ["README.md"] = "hello" };

        var source = await SyncAsync(scheduleId);

        Assert.Equal(SourceType.GitHub, source.SourceType);
        Assert.Null(source.SourceConnectionId);
        Assert.Single(source.Documents);
    }

    [Fact]
    public async Task SharePoint_and_Drive_schedules_still_need_their_connection()
    {
        var scheduleId = await ScheduleAsync(SourceType.GoogleDrive, null);
        var sync = new DocumentSyncService(Db, Submissions, NullLogger<DocumentSyncService>.Instance);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.RunAsync(scheduleId, default));
        Assert.Contains("no connection", error.Message);
    }

    [Fact]
    public async Task Files_with_the_same_name_are_told_apart_by_their_path()
    {
        var scheduleId = await ScheduleAsync(SourceType.GitHub, null);
        Extractor.Files = new() { ["README.md"] = "root readme", ["docs/README.md"] = "docs readme" };
        var first = await SyncAsync(scheduleId);
        Assert.Equal(2, first.AddedDocumentCount);
        await MarkProcessedAsync(first.Id);

        // Only the docs README changes.
        Extractor.Files = new() { ["README.md"] = "root readme", ["docs/README.md"] = "docs readme, revised" };
        var second = await SyncAsync(scheduleId);

        Assert.Equal(0, second.AddedDocumentCount);
        Assert.Equal(1, second.UpdatedDocumentCount);
        Assert.Equal(1, second.UnchangedDocumentCount);

        var documents = await Db.UploadedDocuments.AsNoTracking().Where(x => x.ArtifactId == Artifact.Id).ToListAsync();
        Assert.Equal(["README.md", "docs/README.md"], documents.Select(d => d.RelativePath).Order(StringComparer.Ordinal));
        Assert.Equal(2, documents.Single(d => d.RelativePath == "docs/README.md").Revision);
        Assert.Equal(1, documents.Single(d => d.RelativePath == "README.md").Revision);
    }

    [Fact]
    public async Task Importing_the_same_items_again_reuses_their_records()
    {
        Extractor.WithExternalIds = true;
        Extractor.Files = new() { ["src/a.txt"] = "same", ["src/b.txt"] = "same" };

        var first = await SubmitAndExtractAsync();
        var second = await SubmitAndExtractAsync();

        Assert.NotEqual(SourceDocumentStatus.Failed, second.Status);
        Assert.Equal(2, second.Documents.Count);
        Assert.All(second.Documents, d => Assert.Equal(DocumentStatus.Uploaded, d.Status));
        Assert.Equal(2, await Db.UploadedDocuments.CountAsync(d => d.ArtifactId == Artifact.Id));
        Assert.Empty((await Db.SourceDocuments.Include(x => x.Documents).SingleAsync(x => x.Id == first.Id)).Documents);
    }
}
