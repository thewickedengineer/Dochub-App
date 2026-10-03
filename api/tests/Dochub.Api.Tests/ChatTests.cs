using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Tests;

/// <summary>
/// Chat: the tenant and scope sent to the RAG platform are decided from the
/// database, not the browser, and every turn is saved with its sources.
/// </summary>
public class ChatTests : PipelineTestBase
{
    private readonly FakeRag _rag = new();

    private ChatService Chat => new(Db, _rag, Options.Create(new RagOptions { HistoryMessages = 12 }),
        NullLogger<ChatService>.Instance);

    private async Task<List<JsonObject>> AskAsync(ChatConversation conversation, string question)
    {
        var events = new List<JsonObject>();
        await foreach (var item in Chat.ReplyAsync(conversation, question, default)) events.Add(item);
        return events;
    }

    [Fact]
    public async Task A_team_scope_becomes_the_ids_of_that_teams_artifacts_only()
    {
        var team = await Db.Teams.SingleAsync();
        var otherTeam = new Team { OrganizationId = OrganizationId, Name = "Claims", Slug = "claims", CreatedByUserId = UserId };
        var otherGroup = new Group { Team = otherTeam, Name = "Ops", Slug = "ops", CreatedByUserId = UserId };
        Db.Add(new Artifact { Group = otherGroup, Name = "Runbooks", Slug = "runbooks", Category = "Ops",
                              PrimarySource = SourceType.Local, CreatedByUserId = UserId });
        await Db.SaveChangesAsync();

        var scope = await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Team, team.Id, default);

        Assert.NotNull(scope);
        Assert.Equal("Policy", scope!.Label);
        var ids = scope.Filters["artifact_ids"]!.AsArray().Select(x => (string?)x).ToList();
        Assert.Equal([Artifact.Id.ToString()], ids);
    }

    [Fact]
    public async Task Another_organizations_team_or_artifact_cannot_be_chosen()
    {
        var stranger = new Organization { Name = "Other", Slug = "other", Initials = "OT", CreatedByUserId = UserId };
        var team = new Team { Organization = stranger, Name = "Secret", Slug = "secret", CreatedByUserId = UserId };
        var group = new Group { Team = team, Name = "G", Slug = "g", CreatedByUserId = UserId };
        var artifact = new Artifact { Group = group, Name = "A", Slug = "a", Category = "x",
                                      PrimarySource = SourceType.Local, CreatedByUserId = UserId };
        Db.AddRange(stranger, team, group, artifact);
        await Db.SaveChangesAsync();

        Assert.Null(await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Team, team.Id, default));
        Assert.Null(await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Group, group.Id, default));
        Assert.Null(await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Artifact, artifact.Id, default));
    }

    [Fact]
    public async Task A_turn_is_relayed_with_the_users_tenant_and_saved_with_its_sources()
    {
        var scope = await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Artifact, Artifact.Id, default);
        var conversation = await Chat.CreateAsync(OrganizationId, UserId, scope!, null, default);

        var events = await AskAsync(conversation, "What is the settlement limit for G12?");

        Assert.Equal(["started", "sources", "delta", "delta", "done"], events.Select(e => (string?)e["type"]));
        var request = Assert.Single(_rag.Requests);
        Assert.Equal(OrganizationId.ToString(), (string?)request["tenant_id"]);
        Assert.Equal($"org:{OrganizationId}", (string?)request["principals"]![0]);
        Assert.Equal(Artifact.Id.ToString(), (string?)request["filters"]!["artifact_id"]);

        Db.ChangeTracker.Clear();
        var saved = await Db.ChatMessages.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync();
        Assert.Equal([ChatRole.User, ChatRole.Assistant], saved.Select(m => m.Role));
        var answer = saved[1];
        Assert.Equal("G12 may settle up to 30,000 [1].", answer.Content);
        Assert.Equal([1], JsonSerializer.Deserialize<int[]>(answer.Cited!)!);
        Assert.Equal("fake-chat", answer.Model);
        Assert.Contains("Claims Guide.docx", answer.Sources);
        Assert.Null(answer.Error);
        var titled = await Db.ChatConversations.AsNoTracking().SingleAsync();
        Assert.Equal("What is the settlement limit for G12?", titled.Title);
    }

    [Fact]
    public async Task A_follow_up_carries_the_earlier_turns()
    {
        var scope = await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Organization, null, default);
        var conversation = await Chat.CreateAsync(OrganizationId, UserId, scope!, null, default);
        await AskAsync(conversation, "What is the limit for G12?");
        await AskAsync(conversation, "And for G30?");

        var messages = _rag.Requests[1]["messages"]!.AsArray()
            .Select(m => $"{m!["role"]}: {m["content"]}").ToList();
        Assert.Equal(["user: What is the limit for G12?", "assistant: G12 may settle up to 30,000 [1].",
                      "user: And for G30?"], messages);
        Assert.Empty(_rag.Requests[1]["filters"]!.AsObject());     // organization-wide
    }

    [Fact]
    public async Task A_failure_mid_answer_is_reported_and_saved_with_what_arrived()
    {
        var scope = await Chat.ResolveScopeAsync(OrganizationId, ChatScope.Organization, null, default);
        var conversation = await Chat.CreateAsync(OrganizationId, UserId, scope!, null, default);
        _rag.FailAfterFirstDelta = true;

        var events = await AskAsync(conversation, "Anything?");

        Assert.Equal("error", (string?)events[^1]["type"]);
        Db.ChangeTracker.Clear();
        var answer = await Db.ChatMessages.AsNoTracking().SingleAsync(m => m.Role == ChatRole.Assistant);
        Assert.Equal("G12 ", answer.Content);
        Assert.Equal("The knowledge base is unavailable right now.", answer.Error);

        // A failed turn is not fed back to the model as history.
        _rag.FailAfterFirstDelta = false;
        await AskAsync(conversation, "Try again");
        var history = _rag.Requests[^1]["messages"]!.AsArray().Select(m => (string?)m!["role"]).ToList();
        Assert.Equal(["user", "user"], history);
    }

    private sealed class FakeRag : IRagChatClient
    {
        public List<JsonObject> Requests { get; } = [];
        public bool FailAfterFirstDelta { get; set; }

        public async IAsyncEnumerable<JsonObject> StreamAsync(JsonObject request, [EnumeratorCancellation] CancellationToken ct)
        {
            Requests.Add((JsonObject)request.DeepClone());
            await Task.Yield();
            yield return JsonNode.Parse("""
                {"type":"sources","query":"q","sources":[{"n":1,"filename":"Claims Guide.docx","location":"page 2","snippet":"| G12 | 30,000 |"}]}
                """)!.AsObject();
            yield return new JsonObject { ["type"] = "delta", ["text"] = "G12 " };
            if (FailAfterFirstDelta) throw new HttpRequestException("connection reset");
            yield return new JsonObject { ["type"] = "delta", ["text"] = "may settle up to 30,000 [1]." };
            yield return JsonNode.Parse("""{"type":"done","cited":[1],"model":"fake-chat","usage":{"calls":1}}""")!.AsObject();
        }
    }
}
