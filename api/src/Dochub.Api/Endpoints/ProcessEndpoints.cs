using System.Text.Json;
using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Endpoints;

public static class ProcessEndpoints
{
    /// <summary>Guards against a single request filling the staging disk.</summary>
    private const long MaxStagedFileBytes = 200L * 1024 * 1024;

    public static void MapProcessEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").WithTags("Processing").RequireAuthorization();

        group.MapPost("/uploads/staging", StageFilesAsync)
            .WithSummary("Stream local file bytes to the server, immediately before pressing Process")
            .WithDescription("""
                Only needed for local files, whose bytes the browser cannot hand to the
                extractor directly. Returns staging ids to put in a source's options.
                Nothing is recorded against the artifact until Process is called.
                """)
            .DisableAntiforgery();

        group.MapPost("/artifacts/{artifactId:guid}/process", ProcessAsync)
            .WithSummary("Submit one or more sources for processing")
            .WithDescription("""
                The only call the Upload screen makes. For each source it writes a
                source_documents row with status RequestUpload and puts the source on the
                extract queue. The extractor then moves the files to blob storage
                (Uploading -> Uploaded) and hands them to the process queue, where the
                vector service picks them up (Processing -> Processed).
                """);

        group.MapGet("/source-documents", ListAsync)
            .WithSummary("The processing list for the active organization");

        group.MapGet("/source-documents/{sourceDocumentId:guid}", GetAsync)
            .WithSummary("One submitted source with its per-document detail");

        group.MapPost("/source-documents/{sourceDocumentId:guid}/retry", RetryAsync)
            .WithSummary("Put a failed source back on the extract queue");
    }

    private static async Task<IResult> StageFilesAsync(HttpRequest request, IStagingStore staging, CancellationToken ct)
    {
        if (!request.HasFormContentType)
            return Results.Json(new ApiError("invalid_content_type", "Send the files as multipart/form-data."), statusCode: 415);

        var form = await request.ReadFormAsync(ct);
        if (form.Files.Count == 0)
            return Results.Json(new ApiError("no_files", "No files were included in the request."), statusCode: 400);

        var staged = new List<StagedFileDto>(form.Files.Count);
        foreach (var file in form.Files)
        {
            if (file.Length > MaxStagedFileBytes)
                return Results.Json(new ApiError("file_too_large",
                    $"'{file.FileName}' exceeds the 200 MB per-file limit."), statusCode: 413);

            // The browser sends webkitRelativePath for folder picks; keep it so the
            // source's directory shape survives into the blob prefix.
            var relativePath = form[$"path:{file.Name}"].FirstOrDefault() ?? file.FileName;
            await using var stream = file.OpenReadStream();
            var result = await staging.SaveAsync(file.FileName, relativePath, file.ContentType, stream, ct);
            staged.Add(new StagedFileDto(result.Id, result.OriginalName, result.RelativePath, result.SizeBytes, result.ContentType));
        }

        return Results.Ok(staged);
    }

    private static async Task<IResult> ProcessAsync(
        Guid artifactId, [FromBody] ProcessRequest request, HttpContext http,
        DochubDbContext db, ISourceSubmissionService submissions, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        if (request.Sources is not { Count: > 0 })
            return Results.Json(new ApiError("no_sources",
                "Include at least one source to process."), statusCode: 400);

        var artifact = await db.Artifacts
            .Include(x => x.Group).ThenInclude(g => g.Team)
            .FirstOrDefaultAsync(x => x.Id == artifactId && x.Group.Team.OrganizationId == orgId, ct);
        if (artifact is null)
            return Results.NotFound(new ApiError("not_found", "Artifact not found in this organization."));

        // Validate every source before writing any of them, so a bad entry in a
        // batch does not leave half the submission queued.
        var prepared = new List<SourceSubmission>(request.Sources.Count);
        foreach (var candidate in request.Sources)
        {
            SourceType sourceType;
            try { sourceType = Mapping.ParseSource(candidate.SourceType); }
            catch (ArgumentException ex) { return Results.Json(new ApiError("invalid_source", ex.Message), statusCode: 400); }

            if (sourceType is SourceType.SharePoint or SourceType.GoogleDrive && candidate.SourceConnectionId is null)
                return Results.Json(new ApiError("connection_required",
                    $"{sourceType.Label()} requires a connected account."), statusCode: 400);

            if (candidate.SourceConnectionId is { } connectionId)
            {
                var connection = await db.SourceConnections.FirstOrDefaultAsync(
                    x => x.Id == connectionId && x.OrganizationId == orgId && x.UserId == userId, ct);
                if (connection is null)
                    return Results.Json(new ApiError("connection_not_found",
                        "That source connection does not exist for you in this organization."), statusCode: 400);
                if (connection.Status != ConnectionStatus.Connected)
                    return Results.Json(new ApiError("connection_unusable",
                        $"The {connection.SourceType.Label()} connection is {connection.Status}. Reconnect it and retry."), statusCode: 409);
            }

            if (sourceType == SourceType.Local
                && (candidate.Options is null
                    || !candidate.Options.Value.TryGetProperty("stagingIds", out var ids)
                    || ids.ValueKind != JsonValueKind.Array
                    || ids.GetArrayLength() == 0))
                return Results.Json(new ApiError("nothing_staged",
                    "Local sources need stagingIds from POST /api/uploads/staging."), statusCode: 400);

            if (candidate.Schedule is not null)
            {
                if (!SyncScheduleEndpoints.Syncable.Contains(sourceType))
                    return Results.Json(new ApiError("not_syncable",
                        $"Recurring updates need a SharePoint or Google Drive location. {sourceType.Label()} cannot be re-listed on a schedule."),
                        statusCode: 400);

                var check = SyncScheduleEndpoints.ParseCadence(
                    candidate.Schedule.Frequency, candidate.Schedule.TimeOfDay,
                    candidate.Schedule.TimeZoneId, candidate.Schedule.DayOfWeek, candidate.Schedule.DayOfMonth);
                if (check.Error is not null)
                    return Results.Json(new ApiError("invalid_schedule", check.Error), statusCode: 400);
            }

            prepared.Add(new SourceSubmission(
                sourceType,
                candidate.SourceReference,
                candidate.SourceConnectionId,
                candidate.Options,
                (candidate.Documents ?? [])
                    .Select(d => (d.Name, d.RelativePath, d.SizeBytes, d.ContentType)).ToList()));
        }

        var path = $"{artifact.Group.Team.Name} › {artifact.Group.Name}";
        var requestedBy = await db.Users.Where(x => x.Id == userId)
            .Select(x => x.DisplayName).FirstAsync(ct);

        var submitted = new List<SourceDocumentDto>(prepared.Count);
        var schedules = new List<SyncScheduleDto>();

        for (var index = 0; index < prepared.Count; index++)
        {
            var source = await submissions.SubmitAsync(artifact, orgId, userId, prepared[index], null, ct);
            submitted.Add(source.ToDto(artifact.Name, path, requestedBy));

            // Keeping the location current is opt-in per source, recorded once the
            // source itself is safely queued.
            if (request.Sources[index].Schedule is { } wanted)
                schedules.Add(await UpsertScheduleAsync(db, artifact, orgId, userId, prepared[index], wanted, ct));
        }

        if (schedules.Count > 0) await db.SaveChangesAsync(ct);

        var documentCount = prepared.Sum(x => x.DeclaredDocuments.Count);
        var scheduleNote = schedules.Count switch
        {
            0 => "",
            1 => $" Keeping it current: {schedules[0].Cadence}.",
            _ => $" {schedules.Count} locations will also stay current."
        };

        var message = submitted.Count == 1
            ? $"{submitted[0].Reference} queued · {Describe(prepared[0], documentCount)} from {artifact.Name}. We'll notify you as it progresses."
            : $"{submitted.Count} sources queued for {artifact.Name}. We'll notify you as they progress.";
        message += scheduleNote;

        return Results.Accepted($"/api/source-documents/{submitted[0].Id}",
            new ProcessAcknowledgement(submitted, schedules, message));
    }

    /// <summary>
    /// One schedule per artifact+source: re-submitting the same location re-points
    /// the existing schedule rather than stacking a second one against it.
    /// </summary>
    private static async Task<SyncScheduleDto> UpsertScheduleAsync(
        DochubDbContext db, Artifact artifact, Guid orgId, Guid userId,
        SourceSubmission submission, SyncScheduleRequest wanted, CancellationToken ct)
    {
        var cadence = SyncScheduleEndpoints.ParseCadence(
            wanted.Frequency, wanted.TimeOfDay, wanted.TimeZoneId, wanted.DayOfWeek, wanted.DayOfMonth);

        var schedule = await db.RecurringSyncSchedules
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .Include(x => x.CreatedByUser)
            .FirstOrDefaultAsync(x => x.ArtifactId == artifact.Id && x.SourceType == submission.SourceType, ct);

        if (schedule is null)
        {
            schedule = new RecurringSyncSchedule
            {
                OrganizationId = orgId,
                ArtifactId = artifact.Id,
                SourceType = submission.SourceType,
                CreatedByUserId = userId
            };
            db.RecurringSyncSchedules.Add(schedule);
        }

        schedule.SourceConnectionId = submission.SourceConnectionId!.Value;
        schedule.SourceReference = submission.SourceReference;
        schedule.SourceOptions = submission.Options is { } options
            ? JsonDocument.Parse(options.GetRawText())
            : null;
        schedule.Frequency = cadence.Frequency;
        schedule.TimeOfDay = cadence.TimeOfDay;
        schedule.TimeZoneId = cadence.TimeZoneId;
        schedule.DayOfWeek = cadence.DayOfWeek;
        schedule.DayOfMonth = cadence.DayOfMonth;
        schedule.Status = SyncScheduleStatus.Active;
        schedule.ConsecutiveFailures = 0;
        schedule.LastError = null;
        schedule.NextRunAt = SyncScheduleCalculator.Next(
            cadence.Frequency, cadence.TimeOfDay, cadence.TimeZoneId,
            cadence.DayOfWeek, cadence.DayOfMonth, DateTimeOffset.UtcNow);
        schedule.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        await db.Entry(schedule).Reference(x => x.Artifact).LoadAsync(ct);
        await db.Entry(schedule.Artifact).Reference(x => x.Group).LoadAsync(ct);
        await db.Entry(schedule.Artifact.Group).Reference(x => x.Team).LoadAsync(ct);
        await db.Entry(schedule).Reference(x => x.CreatedByUser).LoadAsync(ct);

        return SyncScheduleEndpoints.ToDto(schedule);
    }

    private static string Describe(SourceSubmission submission, int documentCount) =>
        documentCount > 0
            ? $"{documentCount} document{(documentCount == 1 ? "" : "s")}"
            : submission.SourceReference;

    private static async Task<IResult> ListAsync(
        HttpContext http, DochubDbContext db,
        [FromQuery] int? limit, [FromQuery] Guid? artifactId, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var query = db.SourceDocuments
            .Where(x => x.OrganizationId == orgId)
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .Include(x => x.RequestedByUser)
            .AsNoTracking();

        if (artifactId is { } id) query = query.Where(x => x.ArtifactId == id);

        var sources = await query
            .OrderByDescending(x => x.RequestedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .ToListAsync(ct);

        return Results.Ok(sources.Select(s => s.ToDto(
            s.Artifact.Name,
            $"{s.Artifact.Group.Team.Name} › {s.Artifact.Group.Name}",
            s.RequestedByUser.DisplayName)));
    }

    private static async Task<IResult> GetAsync(Guid sourceDocumentId, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var source = await db.SourceDocuments
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .Include(x => x.RequestedByUser)
            .Include(x => x.Documents)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sourceDocumentId && x.OrganizationId == orgId, ct);

        if (source is null) return Results.NotFound(new ApiError("not_found", "Source not found in this organization."));

        return Results.Ok(new SourceDocumentDetailDto(
            source.ToDto(source.Artifact.Name,
                $"{source.Artifact.Group.Team.Name} › {source.Artifact.Group.Name}",
                source.RequestedByUser.DisplayName),
            source.Documents.OrderBy(d => d.RelativePath).Select(d => d.ToDto()).ToList()));
    }

    private static async Task<IResult> RetryAsync(
        Guid sourceDocumentId, HttpContext http, DochubDbContext db,
        IQueueClient queues, IOptions<ServiceBusOptions> serviceBus, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var source = await db.SourceDocuments
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .FirstOrDefaultAsync(x => x.Id == sourceDocumentId && x.OrganizationId == orgId, ct);
        if (source is null) return Results.NotFound(new ApiError("not_found", "Source not found in this organization."));

        if (source.Status is not (SourceDocumentStatus.Failed or SourceDocumentStatus.PartiallyFailed))
            return Results.Json(new ApiError("not_retryable",
                $"{source.Reference} is {source.Status}; only a failed source can be retried."), statusCode: 409);

        source.Status = SourceDocumentStatus.RequestUpload;
        source.Error = null;
        await db.SaveChangesAsync(ct);

        var artifact = source.Artifact;
        var payload = new SourceUploadRequestedEvent(
            source.Id, source.Reference, artifact.Id, orgId, artifact.Name,
            artifact.Group.Team.Name, artifact.Group.Name, source.SourceType.ToString(),
            source.SourceReference, source.SourceConnectionId,
            source.SourceOptions is null ? null : JsonDocument.Parse(source.SourceOptions.RootElement.GetRawText()).RootElement,
            source.RequestedByUserId, source.RequestedAt,
            source.BlobContainer ?? "", source.BlobPrefix ?? "");

        // A fresh message id: the original was completed or dead-lettered.
        await queues.SendAsync(serviceBus.Value.ExtractQueue,
            $"extract-{source.Id:n}-retry-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
            DochubEvents.SourceUploadRequested, DochubEvents.Serialize(payload), artifact.Id.ToString(), ct);

        return Results.Accepted($"/api/source-documents/{source.Id}",
            new { source.Reference, Message = $"{source.Reference} is back on the extract queue." });
    }
}
