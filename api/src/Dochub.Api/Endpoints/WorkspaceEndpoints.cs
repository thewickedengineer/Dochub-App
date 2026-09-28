using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

/// <summary>Teams → groups → artifacts, plus the knowledge-base rollup.</summary>
public static class WorkspaceEndpoints
{
    public static void MapWorkspaceEndpoints(this IEndpointRouteBuilder app)
    {
        var teams = app.MapGroup("/api/teams").WithTags("Workspace").RequireAuthorization();
        teams.MapGet("/", ListTeamsAsync).WithSummary("Team → group → artifact tree for the active organization");
        teams.MapPost("/", CreateTeamAsync).WithSummary("Create a team — any member may do this");
        teams.MapPost("/{teamId:guid}/groups", CreateGroupAsync).WithSummary("Create a group inside a team");

        var groups = app.MapGroup("/api/groups").WithTags("Workspace").RequireAuthorization();
        groups.MapPost("/{groupId:guid}/artifacts", CreateArtifactAsync).WithSummary("Create an artifact inside a group");

        var artifacts = app.MapGroup("/api/artifacts").WithTags("Workspace").RequireAuthorization();
        artifacts.MapGet("/", ListArtifactsAsync).WithSummary("Flat artifact list — drives the upload destination picker");
        artifacts.MapGet("/{artifactId:guid}", GetArtifactAsync).WithSummary("Artifact with its documents and upload history");

        app.MapGet("/api/knowledge-base", KnowledgeBaseAsync).WithTags("Knowledge base")
            .RequireAuthorization()
            .WithSummary("Current RAG state for the organization — counts, per-artifact coverage and source mix");
    }

    private static async Task<IResult> ListTeamsAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var teams = await db.Teams
            .Where(x => x.OrganizationId == orgId)
            .Include(x => x.Groups).ThenInclude(g => g.Artifacts)
            .OrderBy(x => x.Name).AsNoTracking().ToListAsync(ct);

        var counts = await DocumentCountsAsync(db, orgId, ct);

        return Results.Ok(teams.Select(team => new TeamDto(
            team.Id, team.Name, team.Slug, team.Description,
            team.Groups.Count, team.Groups.Sum(g => g.Artifacts.Count),
            team.Groups.OrderBy(g => g.Name).Select(g => new GroupDto(
                g.Id, g.Name, g.Slug, g.Description,
                g.Artifacts.OrderBy(a => a.Name)
                    .Select(a => Summarize(a, g, team, counts))
                    .ToList())).ToList())));
    }

    private static async Task<IResult> CreateTeamAsync(
        [FromBody] CreateTeamRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Json(new ApiError("invalid_name", "Team name is required."), statusCode: 400);

        var name = request.Name.Trim();
        if (await db.Teams.AnyAsync(x => x.OrganizationId == orgId && x.Name == name, ct))
            return Results.Json(new ApiError("duplicate", $"A team named '{name}' already exists."), statusCode: 409);

        var team = new Team
        {
            OrganizationId = orgId,
            Name = name,
            Slug = Mapping.Slugify(name),
            Description = request.Description?.Trim(),
            CreatedByUserId = http.User.UserId()
        };
        db.Teams.Add(team);
        // The creator joins the team so it shows against them on the Members screen.
        db.TeamMembers.Add(new TeamMember { Team = team, UserId = http.User.UserId(), Role = TeamRole.Lead });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/teams/{team.Id}",
            new TeamDto(team.Id, team.Name, team.Slug, team.Description, 0, 0, []));
    }

    private static async Task<IResult> CreateGroupAsync(
        Guid teamId, [FromBody] CreateGroupRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Json(new ApiError("invalid_name", "Group name is required."), statusCode: 400);

        var team = await db.Teams.FirstOrDefaultAsync(x => x.Id == teamId && x.OrganizationId == orgId, ct);
        if (team is null) return Results.NotFound(new ApiError("not_found", "Team not found in this organization."));

        var name = request.Name.Trim();
        if (await db.Groups.AnyAsync(x => x.TeamId == teamId && x.Name == name, ct))
            return Results.Json(new ApiError("duplicate", $"'{team.Name}' already has a group named '{name}'."), statusCode: 409);

        var group = new Group
        {
            TeamId = teamId,
            Name = name,
            Slug = Mapping.Slugify(name),
            Description = request.Description?.Trim(),
            CreatedByUserId = http.User.UserId()
        };
        db.Groups.Add(group);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/groups/{group.Id}",
            new GroupDto(group.Id, group.Name, group.Slug, group.Description, []));
    }

    private static async Task<IResult> CreateArtifactAsync(
        Guid groupId, [FromBody] CreateArtifactRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Json(new ApiError("invalid_name", "Artifact name is required."), statusCode: 400);

        SourceType source;
        try { source = Mapping.ParseSource(request.PrimarySource); }
        catch (ArgumentException ex) { return Results.Json(new ApiError("invalid_source", ex.Message), statusCode: 400); }

        var group = await db.Groups.Include(x => x.Team)
            .FirstOrDefaultAsync(x => x.Id == groupId && x.Team.OrganizationId == orgId, ct);
        if (group is null) return Results.NotFound(new ApiError("not_found", "Group not found in this organization."));

        var name = request.Name.Trim();
        if (await db.Artifacts.AnyAsync(x => x.GroupId == groupId && x.Name == name, ct))
            return Results.Json(new ApiError("duplicate", $"'{group.Name}' already has an artifact named '{name}'."), statusCode: 409);

        var artifact = new Artifact
        {
            GroupId = groupId,
            Name = name,
            Slug = Mapping.Slugify(name),
            Category = string.IsNullOrWhiteSpace(request.Category) ? "Uncategorized" : request.Category.Trim(),
            PrimarySource = source,
            Description = request.Description?.Trim(),
            CreatedByUserId = http.User.UserId()
        };
        db.Artifacts.Add(artifact);
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/artifacts/{artifact.Id}",
            Mapping.ToSummary(artifact, group, group.Team, []));
    }

    private static async Task<IResult> ListArtifactsAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var counts = await DocumentCountsAsync(db, orgId, ct);

        var artifacts = await db.Artifacts
            .Where(x => x.Group.Team.OrganizationId == orgId)
            .Include(x => x.Group).ThenInclude(g => g.Team)
            .OrderBy(x => x.Group.Team.Name).ThenBy(x => x.Group.Name).ThenBy(x => x.Name)
            .AsNoTracking().ToListAsync(ct);

        return Results.Ok(artifacts.Select(a => Summarize(a, a.Group, a.Group.Team, counts)));
    }

    private static async Task<IResult> GetArtifactAsync(Guid artifactId, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var artifact = await db.Artifacts
            .Include(x => x.Group).ThenInclude(g => g.Team)
            .FirstOrDefaultAsync(x => x.Id == artifactId && x.Group.Team.OrganizationId == orgId, ct);
        if (artifact is null) return Results.NotFound(new ApiError("not_found", "Artifact not found in this organization."));

        var documents = await db.UploadedDocuments
            .Where(x => x.ArtifactId == artifactId)
            .OrderByDescending(x => x.CreatedAt).AsNoTracking().ToListAsync(ct);

        var sources = await db.SourceDocuments
            .Where(x => x.ArtifactId == artifactId)
            .Include(x => x.RequestedByUser)
            .OrderByDescending(x => x.RequestedAt).Take(50).AsNoTracking().ToListAsync(ct);

        var path = $"{artifact.Group.Team.Name} › {artifact.Group.Name}";
        return Results.Ok(new ArtifactDetailDto(
            Mapping.ToSummary(artifact, artifact.Group, artifact.Group.Team, documents),
            documents.Select(d => d.ToDto()).ToList(),
            sources.Select(x => x.ToDto(artifact.Name, path, x.RequestedByUser.DisplayName)).ToList()));
    }

    private static async Task<IResult> KnowledgeBaseAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var counts = await DocumentCountsAsync(db, orgId, ct);

        var artifacts = await db.Artifacts
            .Where(x => x.Group.Team.OrganizationId == orgId)
            .Include(x => x.Group).ThenInclude(g => g.Team)
            .OrderBy(x => x.Group.Team.Name).ThenBy(x => x.Name)
            .AsNoTracking().ToListAsync(ct);

        var summaries = artifacts.Select(a => Summarize(a, a.Group, a.Group.Team, counts)).ToList();

        var bySourceRaw = await db.UploadedDocuments
            .Where(x => x.Artifact.Group.Team.OrganizationId == orgId && x.Status != DocumentStatus.Skipped)
            .GroupBy(x => x.SourceType)
            .Select(g => new { SourceType = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var total = summaries.Sum(x => x.TotalDocuments);
        var indexed = summaries.Sum(x => x.IndexedDocuments);

        var bySource = bySourceRaw
            .OrderByDescending(x => x.Count)
            .Select(x => new SourceBreakdownDto(x.SourceType.ToString(), x.SourceType.Label(), x.Count,
                total == 0 ? 0 : (int)Math.Round(x.Count * 100.0 / total)))
            .ToList();

        var stats = new KnowledgeBaseStatsDto(
            total, indexed,
            summaries.Sum(x => x.PendingDocuments),
            summaries.Sum(x => x.ProcessingDocuments),
            summaries.Sum(x => x.FailedDocuments),
            await db.Teams.CountAsync(x => x.OrganizationId == orgId, ct),
            await db.Groups.CountAsync(x => x.Team.OrganizationId == orgId, ct),
            summaries.Count,
            total == 0 ? 0 : (int)Math.Round(indexed * 100.0 / total));

        var lastIndexed = await db.UploadedDocuments
            .Where(x => x.Artifact.Group.Team.OrganizationId == orgId && x.Status == DocumentStatus.Indexed)
            .MaxAsync(x => (DateTimeOffset?)x.UpdatedAt, ct);

        return Results.Ok(new KnowledgeBaseDto(stats, summaries, bySource, lastIndexed));
    }

    /// <summary>
    /// One grouped query for every artifact's status tallies, so the tree and the
    /// knowledge base render without an N+1 over documents.
    /// </summary>
    private static async Task<Dictionary<Guid, Dictionary<DocumentStatus, int>>> DocumentCountsAsync(
        DochubDbContext db, Guid orgId, CancellationToken ct)
    {
        var rows = await db.UploadedDocuments
            .Where(x => x.Artifact.Group.Team.OrganizationId == orgId)
            .GroupBy(x => new { x.ArtifactId, x.Status })
            .Select(g => new { g.Key.ArtifactId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        return rows.GroupBy(x => x.ArtifactId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Status, x => x.Count));
    }

    private static ArtifactSummaryDto Summarize(
        Artifact artifact, Group group, Team team, Dictionary<Guid, Dictionary<DocumentStatus, int>> counts)
    {
        var byStatus = counts.GetValueOrDefault(artifact.Id) ?? [];
        int Get(DocumentStatus status) => byStatus.GetValueOrDefault(status);

        var indexed = Get(DocumentStatus.Indexed);
        var processing = Get(DocumentStatus.Processing);
        var failed = Get(DocumentStatus.Failed);
        var pending = Get(DocumentStatus.Pending) + Get(DocumentStatus.Uploading) + Get(DocumentStatus.Uploaded);
        var total = indexed + processing + failed + pending;

        var status = total == 0 ? "Empty"
            : processing > 0 ? "Processing"
            : failed > 0 ? "Failed"
            : pending > 0 ? "Pending"
            : "Indexed";

        return new ArtifactSummaryDto(
            artifact.Id, artifact.Name, artifact.Slug, artifact.Category, artifact.PrimarySource.ToString(),
            group.Id, group.Name, team.Id, team.Name,
            total, indexed, pending, processing, failed, status);
    }
}
