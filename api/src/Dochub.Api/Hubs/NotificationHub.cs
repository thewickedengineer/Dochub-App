using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Dochub.Api.Hubs;

public record NotificationPayload(
    Guid Id, string Type, string Title, string Body, string Severity,
    string? EntityType, Guid? EntityId, DateTimeOffset CreatedAt);

public record SourceProgressPayload(
    Guid SourceDocumentId, string Reference, Guid ArtifactId, string Status,
    int TotalDocuments, int UploadedDocuments, int ProcessedDocuments);

public interface INotificationClient
{
    Task Notify(NotificationPayload notification);
    Task SourceProgress(SourceProgressPayload progress);
}

/// <summary>
/// Live channel for toasts, the bell badge and Processing-list progress.
/// Clients join the group for whichever organization they have selected.
/// </summary>
[Authorize]
public class NotificationHub : Hub<INotificationClient>
{
    public static string OrgGroup(Guid organizationId) => $"org:{organizationId}";

    public override async Task OnConnectedAsync()
    {
        // The org comes from the access token, so a client cannot subscribe to another tenant.
        var orgId = Context.User?.FindFirstValue("org_id");
        if (Guid.TryParse(orgId, out var parsed))
            await Groups.AddToGroupAsync(Context.ConnectionId, OrgGroup(parsed));
        await base.OnConnectedAsync();
    }
}
