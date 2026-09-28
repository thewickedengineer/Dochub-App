using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Dochub.Api.Services;

public interface INotificationService
{
    /// <summary>Persists the notification and pushes it live to the org's connected clients.</summary>
    Task RaiseAsync(Guid organizationId, Guid? userId, string type, string title, string body,
        NotificationSeverity severity, string? entityType, Guid? entityId, CancellationToken ct);
}

public class NotificationService(
    DochubDbContext db,
    IHubContext<NotificationHub, INotificationClient> hub,
    ILogger<NotificationService> log) : INotificationService
{
    public async Task RaiseAsync(Guid organizationId, Guid? userId, string type, string title, string body,
        NotificationSeverity severity, string? entityType, Guid? entityId, CancellationToken ct)
    {
        var notification = new Notification
        {
            OrganizationId = organizationId,
            UserId = userId,
            Type = type,
            Title = title,
            Body = body,
            Severity = severity,
            EntityType = entityType,
            EntityId = entityId
        };
        db.Notifications.Add(notification);
        await db.SaveChangesAsync(ct);

        var payload = new NotificationPayload(notification.Id, notification.Type, notification.Title,
            notification.Body, notification.Severity.ToString(), notification.EntityType,
            notification.EntityId, notification.CreatedAt);

        try
        {
            await hub.Clients.Group(NotificationHub.OrgGroup(organizationId)).Notify(payload);
        }
        catch (Exception ex)
        {
            // The row is already committed — clients still pick it up on their next poll.
            log.LogWarning(ex, "Live notification push failed for org {OrgId}", organizationId);
        }
    }
}
