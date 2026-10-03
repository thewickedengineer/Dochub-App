using System.Security.Cryptography;
using System.Text;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Hubs;
using Dochub.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Endpoints;

public record DocumentProcessedRequest(
    Guid SourceDocumentId,
    Guid? DocumentVersionId,
    int ChunkCount,
    string? ContentSha256,
    string? EmbeddingModel,
    string? PipelineVersion,
    /// <summary>True when the vector service found the content already indexed and did no work.</summary>
    bool Skipped);

public record DocumentFailedRequest(
    Guid SourceDocumentId,
    Guid? DocumentVersionId,
    string Error,
    string? Stage,
    /// <summary>False for a transient failure the service will retry; true when it has given up.</summary>
    bool Permanent);

public record ArtifactProcessedRequest(Guid SourceDocumentId);

public record IngestionAck(string Status, string Message);

public record ArtifactProcessedResponse(
    string ArtifactStatus, string SourceStatus, int Processed, int Failed, int Total);

/// <summary>
/// Called by the vector service, not by people. It reports each document as it
/// finishes, and then the whole batch, so Dochub can show where things stand
/// without guessing from the queue.
/// </summary>
public static class IngestionEndpoints
{
    public const string ServiceKeyHeader = "X-Dochub-Service-Key";

    public static void MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ingestion")
            .WithTags("Ingestion callbacks")
            .AllowAnonymous()
            .AddEndpointFilter(RequireServiceKey);

        group.MapPost("/documents/{documentId:guid}/processed", DocumentProcessedAsync)
            .WithSummary("Mark one document processed — vectors stored")
            .WithDescription("""
                Idempotent. Refused with 409 when the version reported is no longer the
                document's current one: a newer upload landed while this one was being
                processed, and its own completion is the one that counts.
                """);

        group.MapPost("/documents/{documentId:guid}/failed", DocumentFailedAsync)
            .WithSummary("Mark one document failed, with the stage and reason");

        group.MapPost("/artifacts/{artifactId:guid}/processed", ArtifactProcessedAsync)
            .WithSummary("Report that every document in a submitted batch has been handled")
            .WithDescription("""
                Closes the source document and recomputes the artifact's status from all
                of its documents — so an artifact is Processed only when everything in it
                is, not merely the batch that just finished.
                """);
    }

    /// <summary>Constant-time comparison so the key cannot be recovered by timing.</summary>
    private static async ValueTask<object?> RequireServiceKey(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<IngestionOptions>>().Value;
        if (!options.IsConfigured)
            return Results.Json(new ApiError("not_configured",
                "Ingestion:ServiceKey is not set (at least 32 characters), so callbacks are refused."), statusCode: 503);

        var presented = context.HttpContext.Request.Headers[ServiceKeyHeader].ToString();
        var expected = Encoding.UTF8.GetBytes(options.ServiceKey!);
        var actual = Encoding.UTF8.GetBytes(presented);

        if (actual.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(actual, expected))
            return Results.Json(new ApiError("unauthorized", $"Missing or wrong {ServiceKeyHeader}."), statusCode: 401);

        return await next(context);
    }

    private static async Task<IResult> DocumentProcessedAsync(
        Guid documentId, [FromBody] DocumentProcessedRequest request,
        DochubDbContext db, IHubContext<NotificationHub, INotificationClient> hub, CancellationToken ct)
    {
        var document = await db.UploadedDocuments.FirstOrDefaultAsync(x => x.Id == documentId, ct);
        if (document is null)
            return Results.NotFound(new ApiError("not_found", "No such document."));

        if (request.DocumentVersionId is { } version && document.CurrentVersionId != version)
            return Results.Json(new ApiError("stale_version",
                "A newer version of this document was uploaded since; its own completion will be reported."),
                statusCode: 409);

        var source = await db.SourceDocuments.FirstOrDefaultAsync(x => x.Id == request.SourceDocumentId, ct);
        if (source is null || source.ArtifactId != document.ArtifactId)
            return Results.Json(new ApiError("source_mismatch",
                "That source document does not belong to this document's artifact."), statusCode: 400);

        document.Status = DocumentStatus.Indexed;
        document.ChunkCount = request.ChunkCount;
        document.ProcessedAt ??= DateTimeOffset.UtcNow;
        document.Error = null;
        document.UpdatedAt = DateTimeOffset.UtcNow;

        // The first document back is the moment processing has visibly started.
        if (source.Status is SourceDocumentStatus.Uploaded or SourceDocumentStatus.RequestUpload)
        {
            source.Status = SourceDocumentStatus.Processing;
            source.ProcessingStartedAt ??= DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync(ct);
        await RecountAsync(db, source, ct);
        await db.SaveChangesAsync(ct);
        await PushAsync(hub, source, ct);

        return Results.Ok(new IngestionAck("Indexed", $"{document.Name} processed ({request.ChunkCount} chunks)."));
    }

    private static async Task<IResult> DocumentFailedAsync(
        Guid documentId, [FromBody] DocumentFailedRequest request,
        DochubDbContext db, IHubContext<NotificationHub, INotificationClient> hub, CancellationToken ct)
    {
        var document = await db.UploadedDocuments.FirstOrDefaultAsync(x => x.Id == documentId, ct);
        if (document is null)
            return Results.NotFound(new ApiError("not_found", "No such document."));

        if (request.DocumentVersionId is { } version && document.CurrentVersionId != version)
            return Results.Json(new ApiError("stale_version",
                "A newer version of this document was uploaded since."), statusCode: 409);

        var source = await db.SourceDocuments.FirstOrDefaultAsync(x => x.Id == request.SourceDocumentId, ct);
        if (source is null || source.ArtifactId != document.ArtifactId)
            return Results.Json(new ApiError("source_mismatch",
                "That source document does not belong to this document's artifact."), statusCode: 400);

        var reason = string.IsNullOrWhiteSpace(request.Stage) ? request.Error : $"{request.Stage}: {request.Error}";

        // A transient failure the service will retry is noted but left in flight.
        // A file that can never be indexed — a vendored library, an icon, an empty
        // file — is skipped rather than failed: nothing went wrong, and nothing to fix.
        if (request.Permanent)
            document.Status = IsSkip(request.Error) ? DocumentStatus.Skipped : DocumentStatus.Failed;
        document.Error = reason.Length > 2000 ? reason[..2000] : reason;
        document.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await RecountAsync(db, source, ct);
        await db.SaveChangesAsync(ct);
        await PushAsync(hub, source, ct);

        return Results.Ok(new IngestionAck(document.Status.ToString(), $"{document.Name}: {reason}"));
    }

    private static async Task<IResult> ArtifactProcessedAsync(
        Guid artifactId, [FromBody] ArtifactProcessedRequest request,
        DochubDbContext db, INotificationService notifications,
        IHubContext<NotificationHub, INotificationClient> hub, CancellationToken ct)
    {
        var artifact = await db.Artifacts.FirstOrDefaultAsync(x => x.Id == artifactId, ct);
        if (artifact is null)
            return Results.NotFound(new ApiError("not_found", "No such artifact."));

        var source = await db.SourceDocuments.FirstOrDefaultAsync(
            x => x.Id == request.SourceDocumentId && x.ArtifactId == artifactId, ct);
        if (source is null)
            return Results.Json(new ApiError("source_mismatch",
                "That source document does not belong to this artifact."), statusCode: 400);

        await RecountAsync(db, source, ct);

        // Anything still uploaded-but-unreported in this batch did not make it.
        var stragglers = await db.UploadedDocuments
            .Where(x => x.SourceDocumentId == source.Id
                        && (x.Status == DocumentStatus.Uploaded || x.Status == DocumentStatus.Processing))
            .ToListAsync(ct);
        foreach (var straggler in stragglers)
        {
            straggler.Status = DocumentStatus.Failed;
            straggler.Error ??= "The vector service finished the batch without reporting this document.";
            straggler.UpdatedAt = DateTimeOffset.UtcNow;
        }
        if (stragglers.Count > 0) await RecountAsync(db, source, ct);

        source.Status = source.FailedDocumentCount == 0
            ? SourceDocumentStatus.Processed
            : source.ProcessedDocumentCount == 0
                ? SourceDocumentStatus.Failed
                : SourceDocumentStatus.PartiallyFailed;
        source.ProcessedAt = DateTimeOffset.UtcNow;
        source.ProcessingStartedAt ??= source.ProcessedAt;

        // The artifact's status is about all of it, not just this batch.
        var states = await db.UploadedDocuments
            .Where(x => x.ArtifactId == artifactId && x.Status != DocumentStatus.Skipped)
            .Select(x => x.Status)
            .ToListAsync(ct);

        artifact.Status = ArtifactStatusFrom(states);
        if (artifact.Status is ArtifactStatus.Processed or ArtifactStatus.PartiallyProcessed)
            artifact.LastProcessedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await PushAsync(hub, source, ct);

        await notifications.RaiseAsync(source.OrganizationId, null, "processing.completed",
            source.Status == SourceDocumentStatus.Processed ? "Processing complete" : "Processing finished with errors",
            $"{source.Reference} · {source.ProcessedDocumentCount} of {source.TotalDocuments} document(s) from " +
            $"{artifact.Name} are searchable" +
            (source.FailedDocumentCount > 0 ? $"; {source.FailedDocumentCount} failed" : "") +
            (await SkippedCountAsync(db, source, ct) is > 0 and var skipped ? $"; {skipped} skipped (nothing to index)." : "."),
            source.Status == SourceDocumentStatus.Processed ? NotificationSeverity.Success : NotificationSeverity.Warning,
            "sourceDocument", source.Id, ct);

        return Results.Ok(new ArtifactProcessedResponse(
            artifact.Status.ToString(), source.Status.ToString(),
            source.ProcessedDocumentCount, source.FailedDocumentCount, source.TotalDocuments));
    }

    /// <summary>The vector service's error codes for content there is nothing to read in.</summary>
    private static readonly HashSet<string> SkipCodes = new(StringComparer.Ordinal)
    {
        "not_indexable", "unsupported_format", "unsupported_in_this_build",
        "no_extractor", "extractor_disabled", "low_quality_extraction", "no_chunks"
    };

    /// <summary>Errors arrive as "code: detail".</summary>
    public static bool IsSkip(string error)
    {
        var colon = error.IndexOf(':');
        return colon > 0 && SkipCodes.Contains(error[..colon].Trim());
    }

    public static ArtifactStatus ArtifactStatusFrom(IReadOnlyCollection<DocumentStatus> states)
    {
        if (states.Count == 0) return ArtifactStatus.Empty;
        if (states.Any(s => s is DocumentStatus.Pending or DocumentStatus.Uploading
                                 or DocumentStatus.Uploaded or DocumentStatus.Processing))
            return ArtifactStatus.Processing;

        var indexed = states.Count(s => s == DocumentStatus.Indexed);
        if (indexed == states.Count) return ArtifactStatus.Processed;
        return indexed == 0 ? ArtifactStatus.Failed : ArtifactStatus.PartiallyProcessed;
    }

    /// <summary>Counts from the rows, so a repeated or out-of-order callback cannot drift them.</summary>
    private static Task<int> SkippedCountAsync(DochubDbContext db, SourceDocument source, CancellationToken ct) =>
        db.UploadedDocuments.CountAsync(x => x.SourceDocumentId == source.Id && x.Status == DocumentStatus.Skipped, ct);

    private static async Task RecountAsync(DochubDbContext db, SourceDocument source, CancellationToken ct)
    {
        var counts = await db.UploadedDocuments
            .Where(x => x.SourceDocumentId == source.Id)
            .GroupBy(x => x.Status)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct);

        source.ProcessedDocumentCount = counts.Where(c => c.Key == DocumentStatus.Indexed).Sum(c => c.Count);
        source.FailedDocumentCount = counts.Where(c => c.Key == DocumentStatus.Failed).Sum(c => c.Count);
    }

    private static Task PushAsync(IHubContext<NotificationHub, INotificationClient> hub, SourceDocument source, CancellationToken ct) =>
        hub.Clients.Group(NotificationHub.OrgGroup(source.OrganizationId))
            .SourceProgress(new SourceProgressPayload(
                source.Id, source.Reference, source.ArtifactId, source.Status.ToString(),
                source.TotalDocuments, source.UploadedDocumentCount, source.ProcessedDocumentCount));
}
