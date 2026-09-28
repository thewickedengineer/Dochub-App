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
    }

    private static async Task<IResult> ListAsync(
        HttpContext http, DochubDbContext db, [FromQuery] int? limit, [FromQuery] bool? unreadOnly, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        // A null user_id is an org-wide broadcast; both kinds land in one feed.
        var query = db.Notifications
            .Where(x => x.OrganizationId == orgId && (x.UserId == null || x.UserId == userId));

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

        var query = db.Notifications
            .Where(x => x.OrganizationId == orgId && (x.UserId == null || x.UserId == userId) && x.ReadAt == null);
        if (ids is { Count: > 0 }) query = query.Where(x => ids.Contains(x.Id));

        var updated = await query.ExecuteUpdateAsync(s => s.SetProperty(x => x.ReadAt, now), ct);
        return Results.Ok(new { MarkedRead = updated });
    }
}
