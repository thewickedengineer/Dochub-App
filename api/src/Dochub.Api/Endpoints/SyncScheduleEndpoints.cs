using System.Text.Json;
using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

public static class SyncScheduleEndpoints
{
    /// <summary>
    /// Only these two sources can be kept in sync. Both expose a stable folder
    /// that can be re-listed on a cadence; a GitHub push or a local drag-and-drop
    /// has no such location to go back to.
    /// </summary>
    public static readonly SourceType[] Syncable = [SourceType.SharePoint, SourceType.GoogleDrive];

    public static void MapSyncScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/sync-schedules").WithTags("Scheduled updates").RequireAuthorization();

        group.MapGet("/", ListAsync)
            .WithSummary("Recurring updates configured in the active organization");
        group.MapPatch("/{scheduleId:guid}", UpdateAsync)
            .WithSummary("Change a schedule's cadence, or pause and resume it");
        group.MapDelete("/{scheduleId:guid}", DeleteAsync)
            .WithSummary("Stop keeping a location in sync");
        group.MapPost("/{scheduleId:guid}/run", RunNowAsync)
            .WithSummary("Run a scheduled update immediately, without waiting for its next slot");
    }

    private static async Task<IResult> ListAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var schedules = await db.RecurringSyncSchedules
            .Where(x => x.OrganizationId == orgId)
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .Include(x => x.CreatedByUser)
            .OrderBy(x => x.NextRunAt)
            .AsNoTracking().ToListAsync(ct);

        return Results.Ok(schedules.Select(ToDto));
    }

    private static async Task<IResult> UpdateAsync(
        Guid scheduleId, [FromBody] UpdateSyncScheduleRequest request,
        HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var schedule = await db.RecurringSyncSchedules
            .Include(x => x.Artifact).ThenInclude(a => a.Group).ThenInclude(g => g.Team)
            .Include(x => x.CreatedByUser)
            .FirstOrDefaultAsync(x => x.Id == scheduleId && x.OrganizationId == orgId, ct);
        if (schedule is null) return Results.NotFound(new ApiError("not_found", "Schedule not found in this organization."));

        if (request.Status is not null)
        {
            if (!Enum.TryParse<SyncScheduleStatus>(request.Status, ignoreCase: true, out var status))
                return Results.Json(new ApiError("invalid_status", "Status must be Active or Paused."), statusCode: 400);
            schedule.Status = status;
            // Resuming after a failure clears the strike count, so the next
            // failure starts the retry budget over rather than tripping instantly.
            if (status == SyncScheduleStatus.Active)
            {
                schedule.ConsecutiveFailures = 0;
                schedule.LastError = null;
            }
        }

        if (request.Frequency is not null || request.TimeOfDay is not null
            || request.TimeZoneId is not null || request.DayOfWeek is not null || request.DayOfMonth is not null)
        {
            var parsed = ParseCadence(
                request.Frequency ?? schedule.Frequency.ToString(),
                request.TimeOfDay ?? schedule.TimeOfDay.ToString("HH:mm"),
                request.TimeZoneId ?? schedule.TimeZoneId,
                request.DayOfWeek ?? schedule.DayOfWeek,
                request.DayOfMonth ?? schedule.DayOfMonth);
            if (parsed.Error is not null) return Results.Json(new ApiError("invalid_schedule", parsed.Error), statusCode: 400);

            schedule.Frequency = parsed.Frequency;
            schedule.TimeOfDay = parsed.TimeOfDay;
            schedule.TimeZoneId = parsed.TimeZoneId;
            schedule.DayOfWeek = parsed.DayOfWeek;
            schedule.DayOfMonth = parsed.DayOfMonth;
        }

        schedule.NextRunAt = SyncScheduleCalculator.Next(
            schedule.Frequency, schedule.TimeOfDay, schedule.TimeZoneId,
            schedule.DayOfWeek, schedule.DayOfMonth, DateTimeOffset.UtcNow);
        schedule.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(schedule));
    }

    private static async Task<IResult> DeleteAsync(Guid scheduleId, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var schedule = await db.RecurringSyncSchedules
            .FirstOrDefaultAsync(x => x.Id == scheduleId && x.OrganizationId == orgId, ct);
        if (schedule is null) return Results.NoContent();

        db.RecurringSyncSchedules.Remove(schedule);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> RunNowAsync(
        Guid scheduleId, HttpContext http, DochubDbContext db,
        IDocumentSyncService sync, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var schedule = await db.RecurringSyncSchedules
            .FirstOrDefaultAsync(x => x.Id == scheduleId && x.OrganizationId == orgId, ct);
        if (schedule is null) return Results.NotFound(new ApiError("not_found", "Schedule not found in this organization."));
        if (schedule.IsRunning)
            return Results.Json(new ApiError("already_running", "That schedule is running right now."), statusCode: 409);

        try
        {
            var outcome = await sync.RunAsync(scheduleId, ct);
            return Results.Accepted($"/api/source-documents/{outcome.SourceDocumentId}", new
            {
                outcome.SourceDocumentId,
                outcome.Reference,
                outcome.Message
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new ApiError("sync_failed", ex.Message), statusCode: 409);
        }
    }

    /// <summary>Validates a cadence and normalizes it, or explains what was wrong.</summary>
    public static (SyncFrequency Frequency, TimeOnly TimeOfDay, string TimeZoneId, int? DayOfWeek, int? DayOfMonth, string? Error)
        ParseCadence(string frequency, string timeOfDay, string? timeZoneId, int? dayOfWeek, int? dayOfMonth)
    {
        if (!Enum.TryParse<SyncFrequency>(frequency, ignoreCase: true, out var parsedFrequency))
            return (default, default, "UTC", null, null, "Frequency must be Daily, Weekly or Monthly.");

        if (!TimeOnly.TryParse(timeOfDay, System.Globalization.CultureInfo.InvariantCulture, out var parsedTime))
            return (default, default, "UTC", null, null, "Time of day must look like 09:00 or 21:30.");

        var zone = string.IsNullOrWhiteSpace(timeZoneId) ? "UTC" : timeZoneId.Trim();
        if (!SyncScheduleCalculator.IsValidZone(zone))
            return (default, default, "UTC", null, null, $"'{zone}' is not a recognized IANA time zone.");

        int? resolvedDayOfWeek = null;
        int? resolvedDayOfMonth = null;

        if (parsedFrequency == SyncFrequency.Weekly)
        {
            var day = dayOfWeek ?? 1;
            if (day is < 0 or > 6)
                return (default, default, "UTC", null, null, "Day of week must be 0 (Sunday) through 6 (Saturday).");
            resolvedDayOfWeek = day;
        }

        if (parsedFrequency == SyncFrequency.Monthly)
        {
            var day = dayOfMonth ?? 1;
            // Capped at 28 so a monthly schedule fires in February too.
            if (day is < 1 or > 28)
                return (default, default, "UTC", null, null, "Day of month must be 1–28, so the schedule fires in every month.");
            resolvedDayOfMonth = day;
        }

        return (parsedFrequency, parsedTime, zone, resolvedDayOfWeek, resolvedDayOfMonth, null);
    }

    public static SyncScheduleDto ToDto(RecurringSyncSchedule s) => new(
        s.Id, s.ArtifactId, s.Artifact.Name,
        $"{s.Artifact.Group.Team.Name} › {s.Artifact.Group.Name}",
        s.SourceType.ToString(), s.SourceReference,
        s.Frequency.ToString(), s.TimeOfDay.ToString("HH:mm"), s.TimeZoneId,
        s.DayOfWeek, s.DayOfMonth, s.Status.ToString(), Describe(s),
        s.NextRunAt, s.LastRunAt,
        s.DocumentsAddedLastRun, s.DocumentsUpdatedLastRun, s.DocumentsUnchangedLastRun,
        s.ConsecutiveFailures, s.LastError, s.CreatedByUser?.DisplayName ?? "—", s.CreatedAt);

    /// <summary>A one-line cadence for the UI, e.g. "Weekly on Monday at 09:00 Europe/London".</summary>
    private static string Describe(RecurringSyncSchedule s)
    {
        var time = s.TimeOfDay.ToString("HH:mm");
        return s.Frequency switch
        {
            SyncFrequency.Daily => $"Daily at {time} {s.TimeZoneId}",
            SyncFrequency.Weekly => $"Weekly on {DayName(s.DayOfWeek ?? 1)} at {time} {s.TimeZoneId}",
            SyncFrequency.Monthly => $"Monthly on day {s.DayOfMonth ?? 1} at {time} {s.TimeZoneId}",
            _ => $"{s.Frequency} at {time}"
        };
    }

    private static string DayName(int day) => day switch
    {
        0 => "Sunday", 1 => "Monday", 2 => "Tuesday", 3 => "Wednesday",
        4 => "Thursday", 5 => "Friday", _ => "Saturday"
    };
}
