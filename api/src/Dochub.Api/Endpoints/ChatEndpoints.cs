using System.Text.Json;
using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

/// <summary>
/// Chat with the knowledge base. Conversations belong to one person in one
/// organization; answers stream back as server-sent events, relayed from the
/// RAG platform with the tenant and scope decided here.
/// </summary>
public static class ChatEndpoints
{
    public static void MapChatEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/chat").WithTags("Chat").RequireAuthorization();

        group.MapGet("/conversations", ListAsync).WithSummary("The caller's conversations in the active organization, newest first");
        group.MapPost("/conversations", CreateAsync).WithSummary("Start a conversation over the organization, a team, a group or an artifact");
        group.MapGet("/conversations/{id:guid}", GetAsync).WithSummary("A conversation with its messages and their sources");
        group.MapPatch("/conversations/{id:guid}", UpdateAsync).WithSummary("Rename a conversation or change what it searches");
        group.MapDelete("/conversations/{id:guid}", DeleteAsync).WithSummary("Delete a conversation");
        group.MapPost("/conversations/{id:guid}/messages", SendAsync)
            .WithSummary("Ask a question. Streams text/event-stream: started, sources, delta…, done (or error)");
    }

    private static ChatConversationDto ToDto(ChatConversation c) =>
        new(c.Id, c.Title, c.Scope.ToString(), c.ScopeId, c.ScopeLabel, c.CreatedAt, c.UpdatedAt);

    private static ChatMessageDto ToDto(ChatMessage m) => new(
        m.Id, m.Role.ToString(), m.Content,
        m.Sources is null ? null : JsonDocument.Parse(m.Sources).RootElement.Clone(),
        m.Cited is null ? null : JsonSerializer.Deserialize<int[]>(m.Cited),
        m.Model, m.Error, m.CreatedAt);

    private static Task<ChatConversation?> FindAsync(DochubDbContext db, HttpContext http, Guid id, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();
        // Someone else's conversation is indistinguishable from a missing one.
        return db.ChatConversations.SingleOrDefaultAsync(c => c.Id == id && c.OrganizationId == orgId && c.UserId == userId, ct);
    }

    private static bool TryParseScope(string? value, out ChatScope scope)
    {
        scope = ChatScope.Organization;
        return string.IsNullOrWhiteSpace(value) || Enum.TryParse(value, ignoreCase: true, out scope);
    }

    private static async Task<IResult> ListAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();
        var items = await db.ChatConversations.AsNoTracking()
            .Where(c => c.OrganizationId == orgId && c.UserId == userId)
            .OrderByDescending(c => c.UpdatedAt).Take(100).ToListAsync(ct);
        return Results.Ok(items.Select(ToDto));
    }

    private static async Task<IResult> CreateAsync(
        CreateChatConversationRequest request, HttpContext http, ChatService chat, CancellationToken ct)
    {
        if (!TryParseScope(request.Scope, out var scope))
            return Results.BadRequest(new ApiError("invalid_scope", "Scope must be Organization, Team, Group or Artifact."));
        var orgId = http.User.RequireOrganizationId();
        var resolved = await chat.ResolveScopeAsync(orgId, scope, request.ScopeId, ct);
        if (resolved is null)
            return Results.NotFound(new ApiError("scope_not_found", $"No such {scope.ToString().ToLowerInvariant()} in this organization."));

        var conversation = await chat.CreateAsync(orgId, http.User.UserId(), resolved, request.Title, ct);
        return Results.Created($"/api/chat/conversations/{conversation.Id}", ToDto(conversation));
    }

    private static async Task<IResult> GetAsync(Guid id, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var conversation = await FindAsync(db, http, id, ct);
        if (conversation is null) return Results.NotFound(new ApiError("not_found", "No such conversation."));
        var messages = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == id).OrderBy(m => m.CreatedAt).ToListAsync(ct);
        return Results.Ok(new ChatConversationDetailDto(ToDto(conversation), messages.Select(ToDto).ToList()));
    }

    private static async Task<IResult> UpdateAsync(
        Guid id, UpdateChatConversationRequest request, HttpContext http, DochubDbContext db, ChatService chat, CancellationToken ct)
    {
        var conversation = await FindAsync(db, http, id, ct);
        if (conversation is null) return Results.NotFound(new ApiError("not_found", "No such conversation."));

        if (!string.IsNullOrWhiteSpace(request.Title))
            conversation.Title = request.Title.Trim().Length > 200 ? request.Title.Trim()[..200] : request.Title.Trim();
        if (request.Scope is not null)
        {
            if (!TryParseScope(request.Scope, out var scope))
                return Results.BadRequest(new ApiError("invalid_scope", "Scope must be Organization, Team, Group or Artifact."));
            var resolved = await chat.ResolveScopeAsync(conversation.OrganizationId, scope, request.ScopeId, ct);
            if (resolved is null)
                return Results.NotFound(new ApiError("scope_not_found", $"No such {scope.ToString().ToLowerInvariant()} in this organization."));
            (conversation.Scope, conversation.ScopeId, conversation.ScopeLabel) = (resolved.Scope, resolved.ScopeId, resolved.Label);
        }
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(conversation));
    }

    private static async Task<IResult> DeleteAsync(Guid id, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var conversation = await FindAsync(db, http, id, ct);
        if (conversation is null) return Results.NotFound(new ApiError("not_found", "No such conversation."));
        db.ChatConversations.Remove(conversation);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task SendAsync(
        Guid id, SendChatMessageRequest request, HttpContext http, DochubDbContext db, ChatService chat, CancellationToken ct)
    {
        var conversation = await FindAsync(db, http, id, ct);
        if (conversation is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(new ApiError("not_found", "No such conversation."), ct);
            return;
        }
        var content = request.Content?.Trim() ?? "";
        if (content.Length is 0 or > 8000)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new ApiError("invalid_request", "Ask a question of 1 to 8000 characters."), ct);
            return;
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await foreach (var item in chat.ReplyAsync(conversation, content, ct))
        {
            await http.Response.WriteAsync($"data: {item.ToJsonString()}\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
        }
    }
}
