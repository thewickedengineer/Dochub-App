using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

/// <summary>
/// The RAG platform's streaming chat endpoint. Events arrive as server-sent
/// events, one JSON object per <c>data:</c> line.
/// </summary>
public interface IRagChatClient
{
    IAsyncEnumerable<JsonObject> StreamAsync(JsonObject request, CancellationToken ct);
}

public sealed class RagChatClient(IHttpClientFactory http, IOptions<IngestionOptions> ingestion) : IRagChatClient
{
    public async IAsyncEnumerable<JsonObject> StreamAsync(JsonObject request, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = http.CreateClient("rag");
        using var message = new HttpRequestMessage(HttpMethod.Post, "chat") { Content = JsonContent.Create(request) };
        message.Headers.Add(Endpoints.IngestionEndpoints.ServiceKeyHeader, ingestion.Value.ServiceKey);

        // Headers first, then read the body as it streams.
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"RAG platform answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}",
                null, response.StatusCode);

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            if (JsonNode.Parse(line[6..]) is JsonObject item) yield return item;
        }
    }
}

public sealed record ResolvedScope(ChatScope Scope, Guid? ScopeId, string Label, JsonObject Filters);

/// <summary>
/// Conversations, scope and the streamed answer. Tenant and permissions are
/// decided here from the signed-in user — never by the browser — and sent to the
/// RAG platform as its tenant and principals.
/// </summary>
public sealed class ChatService(DochubDbContext db, IRagChatClient rag, IOptions<RagOptions> options, ILogger<ChatService> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Null when the scope does not exist in this organization.</summary>
    public async Task<ResolvedScope?> ResolveScopeAsync(Guid organizationId, ChatScope scope, Guid? scopeId, CancellationToken ct)
    {
        switch (scope)
        {
            case ChatScope.Organization:
            {
                var name = await db.Organizations.Where(o => o.Id == organizationId).Select(o => o.Name).SingleAsync(ct);
                return new ResolvedScope(scope, null, name, new JsonObject());
            }
            case ChatScope.Team:
            {
                var team = await db.Teams.AsNoTracking()
                    .SingleOrDefaultAsync(t => t.Id == scopeId && t.OrganizationId == organizationId, ct);
                if (team is null) return null;
                var artifacts = await db.Artifacts.Where(a => a.Group.TeamId == team.Id).Select(a => a.Id).ToListAsync(ct);
                return new ResolvedScope(scope, team.Id, team.Name, ArtifactIds(artifacts));
            }
            case ChatScope.Group:
            {
                var group = await db.Groups.AsNoTracking().Include(g => g.Team)
                    .SingleOrDefaultAsync(g => g.Id == scopeId && g.Team.OrganizationId == organizationId, ct);
                if (group is null) return null;
                var artifacts = await db.Artifacts.Where(a => a.GroupId == group.Id).Select(a => a.Id).ToListAsync(ct);
                return new ResolvedScope(scope, group.Id, $"{group.Team.Name} › {group.Name}", ArtifactIds(artifacts));
            }
            case ChatScope.Artifact:
            {
                var artifact = await db.Artifacts.AsNoTracking().Include(a => a.Group).ThenInclude(g => g.Team)
                    .SingleOrDefaultAsync(a => a.Id == scopeId && a.Group.Team.OrganizationId == organizationId, ct);
                if (artifact is null) return null;
                return new ResolvedScope(scope, artifact.Id, $"{artifact.Group.Team.Name} › {artifact.Group.Name} › {artifact.Name}",
                    new JsonObject { ["artifact_id"] = artifact.Id.ToString() });
            }
            default:
                return null;
        }
    }

    private static JsonObject ArtifactIds(IEnumerable<Guid> ids) =>
        new() { ["artifact_ids"] = new JsonArray(ids.Select(id => (JsonNode)JsonValue.Create(id.ToString())!).ToArray()) };

    public async Task<ChatConversation> CreateAsync(Guid organizationId, Guid userId, ResolvedScope scope, string? title, CancellationToken ct)
    {
        var conversation = new ChatConversation
        {
            OrganizationId = organizationId, UserId = userId, Scope = scope.Scope, ScopeId = scope.ScopeId,
            ScopeLabel = scope.Label, Title = string.IsNullOrWhiteSpace(title) ? "New conversation" : Truncate(title.Trim(), 200)
        };
        db.ChatConversations.Add(conversation);
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    /// <summary>
    /// Saves the question, streams the RAG platform's events through, then saves
    /// the answer with its sources — also when the stream fails or the caller leaves.
    /// </summary>
    public async IAsyncEnumerable<JsonObject> ReplyAsync(
        ChatConversation conversation, string content, [EnumeratorCancellation] CancellationToken ct)
    {
        var scope = await ResolveScopeAsync(conversation.OrganizationId, conversation.Scope, conversation.ScopeId, ct)
            // The team or artifact was deleted since: fall back to the whole organization.
            ?? await ResolveScopeAsync(conversation.OrganizationId, ChatScope.Organization, null, ct);

        var history = await db.ChatMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id && m.Error == null)
            .OrderByDescending(m => m.CreatedAt).Take(options.Value.HistoryMessages)
            .Select(m => new { m.Role, m.Content }).ToListAsync(ct);
        history.Reverse();

        var question = new ChatMessage { ConversationId = conversation.Id, Role = ChatRole.User, Content = content };
        db.ChatMessages.Add(question);
        if (conversation.Title == "New conversation" && !history.Any())
            conversation.Title = Truncate(content.ReplaceLineEndings(" ").Trim(), 80);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var messages = new JsonArray();
        foreach (var turn in history)
            messages.Add(new JsonObject { ["role"] = turn.Role == ChatRole.User ? "user" : "assistant", ["content"] = turn.Content });
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content });

        var request = new JsonObject
        {
            ["tenant_id"] = conversation.OrganizationId.ToString(),
            // The same principal the ingestion adapter grants every document in the org.
            ["principals"] = new JsonArray($"org:{conversation.OrganizationId}"),
            ["messages"] = messages,
            ["filters"] = scope!.Filters.DeepClone(),
        };

        var answer = new ChatMessage { ConversationId = conversation.Id, Role = ChatRole.Assistant };
        var text = new System.Text.StringBuilder();
        string? error = null;

        yield return new JsonObject
        {
            ["type"] = "started", ["questionId"] = question.Id.ToString(), ["answerId"] = answer.Id.ToString(),
            ["scope"] = scope.Label, ["title"] = conversation.Title
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.TimeoutSeconds));
        await using (var events = rag.StreamAsync(request, timeout.Token).GetAsyncEnumerator(timeout.Token))
        {
            while (true)
            {
                JsonObject item;
                try
                {
                    if (!await events.MoveNextAsync()) break;
                    item = events.Current;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    error = "cancelled";
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or IOException)
                {
                    logger.LogWarning(e, "Chat answer failed for conversation {Conversation}", conversation.Id);
                    error = e is OperationCanceledException ? "The answer took too long." : "The knowledge base is unavailable right now.";
                    break;
                }

                switch ((string?)item["type"])
                {
                    case "sources":
                        answer.Sources = item["sources"]?.ToJsonString();
                        break;
                    case "delta":
                        text.Append((string?)item["text"]);
                        break;
                    case "done":
                        answer.Cited = item["cited"]?.ToJsonString();
                        answer.Model = (string?)item["model"];
                        answer.Usage = item["usage"]?.ToJsonString();
                        break;
                    case "error":
                        error = $"{item["error"]}: {item["detail"]}";
                        break;
                }
                yield return item;
            }
        }

        answer.Content = text.ToString();
        answer.Error = error is null ? null : Truncate(error, 2000);
        db.ChatMessages.Add(answer);
        conversation.UpdatedAt = DateTimeOffset.UtcNow;
        // Saved even if the caller has gone: the conversation must show what was said.
        await db.SaveChangesAsync(CancellationToken.None);

        if (error is not null && error != "cancelled")
            yield return new JsonObject { ["type"] = "error", ["error"] = error, ["answerId"] = answer.Id.ToString() };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
