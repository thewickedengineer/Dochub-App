using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Services;

public record SyncOutcome(Guid SourceDocumentId, string Reference, string Message);

/// <summary>
/// Submits a schedule's location for another pass. It deliberately does no I/O
/// of its own: the source goes onto the extract queue like any other, and the
/// extractor reconciles it by file name (by path within the repository, for GitHub). One pipeline, one set of statuses,
/// whether the run was started by a person or by the clock.
/// </summary>
public interface IDocumentSyncService
{
    Task<SyncOutcome> RunAsync(Guid scheduleId, CancellationToken ct);
}

public class DocumentSyncService(
    DochubDbContext db,
    ISourceSubmissionService submissions,
    ILogger<DocumentSyncService> log) : IDocumentSyncService
{
    public async Task<SyncOutcome> RunAsync(Guid scheduleId, CancellationToken ct)
    {
        var schedule = await db.RecurringSyncSchedules
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .FirstOrDefaultAsync(x => x.Id == scheduleId, ct)
            ?? throw new InvalidOperationException($"Sync schedule {scheduleId} no longer exists.");

        // A public GitHub repository is read anonymously; everything else needs its grant.
        if (schedule.SourceConnectionId is null)
        {
            if (SyncScheduleEndpoints.NeedsConnection(schedule.SourceType))
                throw new InvalidOperationException(
                    $"This {schedule.SourceType.Label()} schedule has no connection. Reconnect the source.");
        }
        else
        {
            var connection = await db.SourceConnections
                .FirstOrDefaultAsync(x => x.Id == schedule.SourceConnectionId, ct)
                ?? throw new InvalidOperationException("The connection behind this schedule no longer exists. Reconnect the source.");

            // Checked here rather than in the extractor so a dead connection fails the
            // run immediately, instead of after a message round trip.
            if (connection.Status != ConnectionStatus.Connected || connection.ProtectedAccessToken is null)
                throw new InvalidOperationException(
                    $"The {schedule.SourceType.Label()} connection is {connection.Status}. Reconnect it to resume syncing.");
            if (connection.ExpiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException(
                    $"The {schedule.SourceType.Label()} token expired on {expiry:u}. Reconnect it to resume syncing.");
        }

        // A run still in flight means the previous one has not finished; starting
        // another would race it for the same documents.
        var inFlight = await db.SourceDocuments.AnyAsync(
            x => x.SyncScheduleId == schedule.Id &&
                 x.Status < SourceDocumentStatus.Processed, ct);
        if (inFlight)
            throw new InvalidOperationException(
                $"The previous run for {schedule.Artifact.Name} has not finished yet.");

        var submission = new SourceSubmission(
            schedule.SourceType,
            schedule.SourceReference,
            schedule.SourceConnectionId,
            schedule.SourceOptions?.RootElement,
            []);

        var source = await submissions.SubmitAsync(
            schedule.Artifact, schedule.OrganizationId, schedule.CreatedByUserId,
            submission, schedule.Id, ct);

        schedule.LastRunAt = DateTimeOffset.UtcNow;
        schedule.LastSourceDocumentId = source.Id;
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Schedule {Schedule} submitted {Reference}", schedule.Id, source.Reference);

        return new SyncOutcome(source.Id, source.Reference,
            $"{source.Reference} queued. Files are matched by {(schedule.SourceType == SourceType.GitHub ? "path" : "name")}, so only changed documents are re-vectorized.");
    }
}
