using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Workers;

/// <summary>
/// Drives the recurring source syncs: finds schedules whose next run is due,
/// claims each one so a slow run cannot be started twice, hands it to
/// <see cref="IDocumentSyncService"/>, and rolls the schedule forward.
///
/// This is the standing "keep the vector store current" service. It can run in
/// this host or, by setting Sync:Enabled=false here and true in a second
/// deployment, entirely on its own — it shares only the database with the API.
/// </summary>
public class RecurringSyncWorker(
    IServiceScopeFactory scopes,
    IOptions<SyncOptions> options,
    ILogger<RecurringSyncWorker> log) : BackgroundService
{
    /// <summary>After this many consecutive failures the schedule stops retrying and asks for attention.</summary>
    private const int FailureLimit = 5;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            log.LogInformation("Recurring sync worker disabled (Sync:Enabled=false).");
            return;
        }

        log.LogInformation("Recurring sync worker started — polling every {Seconds}s", settings.PollIntervalSeconds);
        await ReleaseAbandonedClaimsAsync(ct);

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, settings.PollIntervalSeconds)));
        while (await timer.WaitForNextTickAsync(ct))
        {
            try { await TickAsync(settings, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Recurring sync sweep failed"); }
        }
    }

    private async Task TickAsync(SyncOptions settings, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
        var now = DateTimeOffset.UtcNow;

        var due = await db.RecurringSyncSchedules
            .Where(x => x.Status == SyncScheduleStatus.Active && !x.IsRunning && x.NextRunAt <= now)
            .OrderBy(x => x.NextRunAt)
            .Take(settings.MaxPerSweep)
            .ToListAsync(ct);

        foreach (var schedule in due)
        {
            // Claim, and roll the schedule forward in the same write. Even if this
            // run dies, the next occurrence is already booked rather than the
            // schedule re-firing on every sweep.
            var claimed = await db.RecurringSyncSchedules
                .Where(x => x.Id == schedule.Id && !x.IsRunning && x.Status == SyncScheduleStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsRunning, true)
                    .SetProperty(x => x.RunStartedAt, now)
                    .SetProperty(x => x.NextRunAt, SyncScheduleCalculator.Next(
                        schedule.Frequency, schedule.TimeOfDay, schedule.TimeZoneId,
                        schedule.DayOfWeek, schedule.DayOfMonth, now)), ct);

            if (claimed == 0) continue;   // another instance got there first
            await RunOneAsync(schedule.Id, ct);
        }
    }

    private async Task RunOneAsync(Guid scheduleId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
        var sync = scope.ServiceProvider.GetRequiredService<IDocumentSyncService>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();

        try
        {
            var outcome = await sync.RunAsync(scheduleId, ct);
            await db.RecurringSyncSchedules
                .Where(x => x.Id == scheduleId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsRunning, false)
                    .SetProperty(x => x.RunStartedAt, (DateTimeOffset?)null)
                    .SetProperty(x => x.ConsecutiveFailures, 0)
                    .SetProperty(x => x.LastError, (string?)null), ct);

            log.LogInformation("Schedule {Schedule} submitted {Reference}", scheduleId, outcome.Reference);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Schedule {Schedule} failed", scheduleId);
            var message = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;

            var schedule = await db.RecurringSyncSchedules
                .Include(x => x.Artifact)
                .FirstOrDefaultAsync(x => x.Id == scheduleId, ct);
            if (schedule is null) return;

            schedule.IsRunning = false;
            schedule.RunStartedAt = null;
            schedule.ConsecutiveFailures++;
            schedule.LastError = message;
            schedule.UpdatedAt = DateTimeOffset.UtcNow;

            // Most failures here are an expired token. Retrying every hour forever
            // would just spam; after the limit the schedule parks itself and says so.
            if (schedule.ConsecutiveFailures >= FailureLimit)
            {
                schedule.Status = SyncScheduleStatus.Failed;
                await db.SaveChangesAsync(ct);
                await notifications.RaiseAsync(schedule.OrganizationId, null, "sync.failed",
                    "Scheduled updates paused",
                    $"{schedule.Artifact.Name} failed to sync {FailureLimit} times and has been paused. {message}",
                    NotificationSeverity.Error, "syncSchedule", schedule.Id, ct);
            }
            else
            {
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>Clears claims left behind by a process that died mid-run.</summary>
    private async Task ReleaseAbandonedClaimsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<DochubDbContext>();
            var cutoff = DateTimeOffset.UtcNow.AddHours(-2);
            var released = await db.RecurringSyncSchedules
                .Where(x => x.IsRunning && (x.RunStartedAt == null || x.RunStartedAt < cutoff))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.IsRunning, false)
                    .SetProperty(x => x.RunStartedAt, (DateTimeOffset?)null), ct);
            if (released > 0) log.LogWarning("Released {Count} abandoned sync claim(s)", released);
        }
        catch (Exception ex) { log.LogError(ex, "Could not release abandoned sync claims"); }
    }
}
