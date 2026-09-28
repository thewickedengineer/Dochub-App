using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Tests;

/// <summary>
/// Covers the contract the pipeline promises: pressing Process records the source
/// and queues it, the extractor moves the bytes and hands them on, and the two
/// queues carry what each consumer needs.
/// </summary>
public class PipelineFlowTests : PipelineTestBase
{
    [Fact]
    public async Task Submitting_records_the_source_as_request_upload_and_queues_it()
    {
        var submission = new SourceSubmission(
            SourceType.GoogleDrive, "Drive › Policy", Connection.Id,
            System.Text.Json.JsonDocument.Parse("""{"folderId":"root"}""").RootElement, []);

        var source = await Submissions.SubmitAsync(Artifact, OrganizationId, UserId, submission, null, default);

        Assert.Equal(SourceDocumentStatus.RequestUpload, source.Status);
        Assert.StartsWith("SRC-", source.Reference);

        // Exactly one message, on the extract queue, and nothing on the process queue yet.
        var queued = Assert.Single(Queues.On(ExtractQueue));
        Assert.Equal(DochubEvents.SourceUploadRequested, queued.Subject);
        Assert.Empty(Queues.On(ProcessQueue));

        // Nothing has been uploaded at this point.
        Assert.Empty(Blobs.Blobs);
    }

    [Fact]
    public async Task The_extract_message_carries_everything_the_extractor_needs()
    {
        var submission = new SourceSubmission(
            SourceType.GoogleDrive, "Drive › Policy", Connection.Id,
            System.Text.Json.JsonDocument.Parse("""{"folderId":"shared-drive-42"}""").RootElement, []);

        await Submissions.SubmitAsync(Artifact, OrganizationId, UserId, submission, null, default);

        var request = DochubEvents.Deserialize<SourceUploadRequestedEvent>(
            Queues.On(ExtractQueue).Single().Payload);

        Assert.Equal(Artifact.Id, request.ArtifactId);
        Assert.Equal(OrganizationId, request.OrganizationId);
        Assert.Equal(Connection.Id, request.SourceConnectionId);
        Assert.Equal("GoogleDrive", request.SourceType);
        Assert.Equal("shared-drive-42", request.SourceOptions!.Value.GetProperty("folderId").GetString());
        Assert.StartsWith("teams/policy/groups/admin/artifacts/policy-forms/", request.BlobPrefix);
    }

    [Fact]
    public async Task Extracting_uploads_the_files_and_hands_them_to_the_process_queue()
    {
        Extractor.Files = new()
        {
            ["HO-3 Homeowners.docx"] = "homeowners text",
            ["Personal Auto.pdf"] = "auto text",
        };

        var source = await SubmitAndExtractAsync();

        Assert.Equal(SourceDocumentStatus.Uploaded, source.Status);
        Assert.Equal(2, source.UploadedDocumentCount);
        Assert.NotNull(source.UploadStartedAt);
        Assert.NotNull(source.UploadedAt);
        Assert.Equal(2, Blobs.Blobs.Count);
        Assert.All(source.Documents, d => Assert.Equal(DocumentStatus.Uploaded, d.Status));

        var handed = Assert.Single(Queues.On(ProcessQueue));
        Assert.Equal(DochubEvents.DocumentsProcessRequested, handed.Subject);

        var request = DochubEvents.Deserialize<DocumentsProcessRequestedEvent>(handed.Payload);
        Assert.Equal(2, request.DocumentCount);
        Assert.All(request.Documents, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.BlobPath));
            Assert.False(string.IsNullOrWhiteSpace(d.ChecksumSha256));
        });
    }

    [Fact]
    public async Task The_process_message_never_carries_content_or_credentials()
    {
        Extractor.Files = new() { ["Secret.docx"] = "the actual document text" };
        await SubmitAndExtractAsync();

        var payload = Queues.On(ProcessQueue).Single().Payload;

        // The consumer is given a pointer and fetches the bytes with its own credentials.
        Assert.DoesNotContain("the actual document text", payload);
        Assert.DoesNotContain("token", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Both_messages_share_the_artifact_as_their_session_so_work_stays_ordered()
    {
        Extractor.Files = new() { ["Doc.txt"] = "one" };
        await SubmitAndExtractAsync();

        Assert.All(Queues.Messages, m => Assert.Equal(Artifact.Id.ToString(), m.SessionId));
    }

    [Fact]
    public async Task A_source_that_yields_nothing_fails_rather_than_queueing_empty_work()
    {
        Extractor.Files = [];

        var source = await SubmitAndExtractAsync();

        Assert.Equal(SourceDocumentStatus.Failed, source.Status);
        Assert.Contains("no documents", source.Error!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Queues.On(ProcessQueue));
    }

    [Fact]
    public async Task A_redelivered_message_does_not_upload_the_same_files_twice()
    {
        Extractor.Files = new() { ["Doc.txt"] = "one" };
        var source = await SubmitAndExtractAsync();
        var blobsAfterFirstRun = Blobs.Blobs.Count;

        // The broker redelivers after the work already landed.
        var request = DochubEvents.Deserialize<SourceUploadRequestedEvent>(
            Queues.On(ExtractQueue).Single().Payload);
        await Extraction.HandleAsync(request, default);

        Assert.Equal(blobsAfterFirstRun, Blobs.Blobs.Count);
        Assert.Single(Queues.On(ProcessQueue));
    }

    [Fact]
    public async Task Progress_spans_upload_and_processing_so_the_bar_never_restarts()
    {
        Extractor.Files = new() { ["A.txt"] = "a", ["B.txt"] = "b" };
        var source = await SubmitAndExtractAsync();

        // Uploaded but not yet vectorized: half way.
        Assert.Equal(50, Dochub.Api.Endpoints.Mapping.ProgressPercent(source));

        await MarkProcessedAsync(source.Id);
        var processed = await Db.SourceDocuments.SingleAsync(x => x.Id == source.Id);
        Assert.Equal(100, Dochub.Api.Endpoints.Mapping.ProgressPercent(processed));
    }
}
