using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Dochub.Api.Tests;

/// <summary>
/// Spins up a throwaway Postgres database per test class. The schema leans on
/// jsonb, partial indexes, a sequence and SKIP LOCKED, none of which an in-memory
/// provider models, so the pipeline is exercised against the real thing.
/// </summary>
public abstract class PipelineTestBase : IAsyncLifetime
{
    private const string AdminConnection =
        "Host=localhost;Port=5432;Database=postgres;Username=dochub;Password=dochub";

    protected const string ExtractQueue = "test-extract";
    protected const string ProcessQueue = "test-process";

    private readonly string _database = $"dochub_test_{Guid.NewGuid():n}";

    protected DochubDbContext Db = default!;
    protected FakeBlobStorage Blobs = default!;
    protected StubExtractor Extractor = default!;
    protected FakeQueueClient Queues = default!;
    protected RecordingNotificationService Notifications = default!;
    protected SourceSubmissionService Submissions = default!;
    protected ExtractorService Extraction = default!;

    protected Artifact Artifact = default!;
    protected Guid OrganizationId;
    protected Guid UserId;
    protected SourceConnection Connection = default!;

    protected static IOptions<ServiceBusOptions> QueueOptions => Options.Create(new ServiceBusOptions
    {
        ExtractQueue = ExtractQueue,
        ProcessQueue = ProcessQueue
    });

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(AdminConnection))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{_database}\"";
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<DochubDbContext>()
            .UseNpgsql($"Host=localhost;Port=5432;Database={_database};Username=dochub;Password=dochub",
                npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "dochub"))
            .Options;

        Db = new DochubDbContext(options);
        await Db.Database.MigrateAsync();

        Blobs = new FakeBlobStorage();
        Extractor = new StubExtractor(SourceType.GoogleDrive);
        Queues = new FakeQueueClient();
        Notifications = new RecordingNotificationService();

        Submissions = new SourceSubmissionService(
            Db, Blobs, Queues, QueueOptions, NullLogger<SourceSubmissionService>.Instance);

        Extraction = new ExtractorService(
            Db, Blobs, new StubExtractorFactory(Extractor), new NoopStagingStore(),
            new StubTokenProvider(), Notifications, Queues, QueueOptions,
            NullLogger<ExtractorService>.Instance);

        await SeedAsync();
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(AdminConnection);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    private async Task SeedAsync()
    {
        var user = new User
        {
            Email = "owner@test.local", DisplayName = "Test Owner",
            IdentityProvider = "test", ExternalSubject = Guid.NewGuid().ToString("n")
        };
        var organization = new Organization
        {
            Name = "Test Org", Slug = "test-org", Initials = "TO", CreatedByUserId = user.Id
        };
        var team = new Team { Organization = organization, Name = "Policy", Slug = "policy", CreatedByUserId = user.Id };
        var group = new Group { Team = team, Name = "Admin", Slug = "admin", CreatedByUserId = user.Id };
        var artifact = new Artifact
        {
            Group = group, Name = "Policy Forms", Slug = "policy-forms",
            Category = "Policy documents", PrimarySource = SourceType.GoogleDrive, CreatedByUserId = user.Id
        };
        var connection = new SourceConnection
        {
            Organization = organization, UserId = user.Id, SourceType = SourceType.GoogleDrive,
            DisplayName = "Google Drive", Status = ConnectionStatus.Connected,
            ProtectedAccessToken = "token"
        };

        Db.AddRange(user, organization, team, group, artifact, connection);
        await Db.SaveChangesAsync();

        Artifact = artifact;
        OrganizationId = organization.Id;
        UserId = user.Id;
        Connection = connection;
    }

    /// <summary>Submits a source and runs the extractor over it, as the queue would.</summary>
    protected async Task<SourceDocument> SubmitAndExtractAsync(Guid? scheduleId = null)
    {
        var submission = new SourceSubmission(
            SourceType.GoogleDrive, "Drive › Policy", Connection.Id,
            System.Text.Json.JsonDocument.Parse("""{"folderId":"root"}""").RootElement, []);

        var source = await Submissions.SubmitAsync(Artifact, OrganizationId, UserId, submission, scheduleId, default);

        var message = Queues.On(ExtractQueue).Last();
        var request = DochubEvents.Deserialize<SourceUploadRequestedEvent>(message.Payload);
        await Extraction.HandleAsync(request, default);

        Db.ChangeTracker.Clear();
        return await Db.SourceDocuments.Include(x => x.Documents).SingleAsync(x => x.Id == source.Id);
    }

    /// <summary>Stands in for the vector service finishing its work.</summary>
    protected async Task MarkProcessedAsync(Guid sourceDocumentId)
    {
        await Db.UploadedDocuments
            .Where(x => x.SourceDocumentId == sourceDocumentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, DocumentStatus.Indexed));
        await Db.SourceDocuments
            .Where(x => x.Id == sourceDocumentId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, SourceDocumentStatus.Processed));
        Db.ChangeTracker.Clear();
    }
}

/// <summary>Local staging is irrelevant to these tests, which drive connector sources.</summary>
public class NoopStagingStore : IStagingStore
{
    public Task<StagedFile> SaveAsync(string originalName, string? relativePath, string? contentType, Stream content, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task<StagedFile?> GetAsync(string id, CancellationToken ct) => Task.FromResult<StagedFile?>(null);
    public Task DeleteAsync(string id, CancellationToken ct) => Task.CompletedTask;
}
