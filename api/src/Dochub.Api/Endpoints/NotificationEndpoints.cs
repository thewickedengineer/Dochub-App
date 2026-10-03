using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

public static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("/", ListAsync).WithSummary("Notifications for the caller, newest first");
        group.MapPost("/read", MarkReadAsync).WithSummary("Mark notifications read — all of them when no ids are given");
        group.MapDelete("/{id:guid}", ClearOneAsync).WithSummary("Clear one notification — for the caller only");
        group.MapDelete("/", ClearAllAsync).WithSummary("Clear every notification — for the caller only");
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, DochubDbContext db, [FromQuery] int? limit, [FromQuery] bool? unreadOnly, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        // A null user_id is an org-wide broadcast; both kinds land in one feed.
        var query = Visible(db, orgId, userId);

        var unreadCount = await query.CountAsync(x => x.ReadAt == null, ct);
        if (unreadOnly == true) query = query.Where(x => x.ReadAt == null);

        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(Math.Clamp(limit ?? 50, 1, 200))
            .AsNoTracking().ToListAsync(ct);

        return Results.Ok(new NotificationListDto(items.Select(x => x.ToDto()).ToList(), unreadCount));
    }

    private static async Task<IResult> MarkReadAsync(
        [FromBody] IReadOnlyList<Guid>? ids, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();
        var now = DateTimeOffset.UtcNow;

        var query = Visible(db, orgId, userId).Where(x => x.ReadAt == null);
        if (ids is { Count: > 0 }) query = query.Where(x => ids.Contains(x.Id));

        var updated = await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
        return Results.Ok(new { MarkedRead = updated });
    }

    /// <summary>The caller's feed: org-wide and personal notifications they haven't cleared.</summary>
    private static IQueryable<Domain.Notification> Visible(DochubDbContext db, Guid orgId, Guid userId) =>
        db.Notifications.Where(x => x.OrganizationId == orgId && (x.UserId == null || x.UserId == userId)
            && !db.NotificationDismissals.Any(d => d.NotificationId == x.Id && d.UserId == userId));

    private static async Task<IResult> ClearOneAsync(Guid id, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();
        if (!await Visible(db, orgId, userId).AnyAsync(x => x.Id == id, ct))
            return Results.NoContent();   // already cleared, or never theirs: the outcome is the same
        db.NotificationDismissals.Add(new Domain.NotificationDismissal { NotificationId = id, UserId = userId });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ClearAllAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();
        var ids = await Visible(db, orgId, userId).Select(x => x.Id).ToListAsync(ct);
        db.NotificationDismissals.AddRange(ids.Select(id => new Domain.NotificationDismissal { NotificationId = id, UserId = userId }));
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { Cleared = ids.Count });
    }
}
