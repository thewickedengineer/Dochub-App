using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static void MapOrganizationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/organizations").WithTags("Organizations").RequireAuthorization();

        group.MapGet("/", ListAsync).WithSummary("Organizations the caller belongs to");
        group.MapPost("/", CreateAsync).WithSummary("Create an organization (requires the create-organizations privilege)");
        group.MapGet("/current/members", ListMembersAsync).WithSummary("Members of the active organization");
        group.MapPost("/current/members", AddMemberAsync).WithSummary("Add a member — owner only");
        group.MapPatch("/current/members/{userId:guid}", UpdateMemberAsync).WithSummary("Change a member's level — owner only");
        group.MapDelete("/current/members/{userId:guid}", RemoveMemberAsync).WithSummary("Remove a member — owner only");
    }

    private static async Task<IResult> ListAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var rows = await db.OrganizationMembers
            .Where(x => x.UserId == userId)
            .Select(x => new
            {
                x.Organization.Id, x.Organization.Name, x.Organization.Slug,
                x.Organization.Initials, x.Organization.Plan, x.Role,
                MemberCount = x.Organization.Members.Count
            })
            .OrderBy(x => x.Name).ToListAsync(ct);

        return Results.Ok(rows.Select(x =>
            new OrganizationDto(x.Id, x.Name, x.Slug, x.Initials, x.Plan, x.Role.ToString(), x.MemberCount)));
    }

    private static async Task<IResult> CreateAsync(
        [FromBody] CreateOrganizationRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            return Results.Json(new ApiError("invalid_name", "Organization name is required."), statusCode: 400);

        var userId = http.User.UserId();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return Results.Unauthorized();

        // Creating an org is a level-gated action: the platform flag, or Owner/Admin somewhere already.
        var privileged = user.CanCreateOrganizations || await db.OrganizationMembers
            .AnyAsync(x => x.UserId == userId && (x.CanCreateOrganizations || x.Role == OrgRole.Owner || x.Role == OrgRole.Admin), ct);
        if (!privileged)
            return Results.Json(new ApiError("insufficient_level",
                "Creating organizations requires Owner or Admin level in an existing organization."), statusCode: 403);

        // The owner is whoever the request names by their sign-in id; else the caller.
        var owner = user;
        if (!string.IsNullOrWhiteSpace(request.OwnerLoginId))
        {
            var loginId = request.OwnerLoginId.Trim().ToLowerInvariant();
            if (!PlatformEndpoints.IsLoginId(loginId))
                return Results.Json(new ApiError("invalid_owner",
                    "The owner must be given by the address they sign in with, e.g. name@company.com."), statusCode: 400);
            owner = await PlatformEndpoints.FindOrInviteAsync(db, loginId, request.OwnerDisplayName, ct);
        }

        var slug = await UniqueSlugAsync(db, Mapping.Slugify(request.Name), ct);
        var organization = new Organization
        {
            Name = request.Name.Trim(),
            Slug = slug,
            Initials = Mapping.Initials(request.Name),
            Plan = string.IsNullOrWhiteSpace(request.Plan) ? "Trial" : request.Plan.Trim(),
            CreatedByUserId = userId
        };
        db.Organizations.Add(organization);

        // The owner is the only level that can add members. When someone else is
        // named, the caller is not added: a Creator sets organizations up for others.
        db.OrganizationMembers.Add(new OrganizationMember
        {
            Organization = organization,
            UserId = owner.Id,
            Role = OrgRole.Owner,
            CanCreateOrganizations = true,
            InvitedByUserId = owner.Id == userId ? null : userId
        });

        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/organizations/{organization.Id}",
            new OrganizationDto(organization.Id, organization.Name, organization.Slug,
                organization.Initials, organization.Plan, owner.Id == userId ? nameof(OrgRole.Owner) : "", 1));
    }

    private static async Task<IResult> ListMembersAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();

        var members = await db.OrganizationMembers
            .Where(x => x.OrganizationId == orgId)
            .Include(x => x.User)
            .OrderByDescending(x => x.Role).ThenBy(x => x.User.DisplayName)
            .ToListAsync(ct);

        var teamsByUser = await db.TeamMembers
            .Where(x => x.Team.OrganizationId == orgId)
            .Select(x => new { x.UserId, x.Team.Name })
            .ToListAsync(ct);

        var lookup = teamsByUser.GroupBy(x => x.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Name).OrderBy(x => x).ToList());

        return Results.Ok(members.Select(m => new MemberDto(
            m.UserId, m.User.DisplayName, m.User.Email, m.Role.ToString(), m.User.IdentityProvider,
            m.CanCreateOrganizations || m.Role is OrgRole.Owner or OrgRole.Admin,
            // "All teams" mirrors the design's shorthand for an owner.
            m.Role == OrgRole.Owner ? ["All teams"] : lookup.GetValueOrDefault(m.UserId, []),
            m.JoinedAt)));
    }

    private static async Task<IResult> AddMemberAsync(
        [FromBody] AddMemberRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        if (http.User.Role() != OrgRole.Owner)
            return Results.Json(new ApiError("owner_only",
                "Only the organization owner can add members."), statusCode: 403);

        if (string.IsNullOrWhiteSpace(request.Email) || !request.Email.Contains('@'))
            return Results.Json(new ApiError("invalid_email", "A valid email address is required."), statusCode: 400);
        if (!Enum.TryParse<OrgRole>(request.Role, ignoreCase: true, out var role))
            return Results.Json(new ApiError("invalid_role", "Role must be Member, Admin or Owner."), statusCode: 400);

        var email = request.Email.Trim().ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Email == email, ct);

        if (user is null)
        {
            // Placeholder identity: the row binds to a real provider subject on first SSO sign-in.
            user = new User
            {
                Email = email,
                DisplayName = request.DisplayName?.Trim() is { Length: > 0 } name ? name : DeriveName(email),
                IdentityProvider = "invited",
                ExternalSubject = $"invite:{Guid.NewGuid():n}"
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
        }

        if (await db.OrganizationMembers.AnyAsync(x => x.OrganizationId == orgId && x.UserId == user.Id, ct))
            return Results.Json(new ApiError("already_member", $"{email} is already in this organization."), statusCode: 409);

        db.OrganizationMembers.Add(new OrganizationMember
        {
            OrganizationId = orgId,
            UserId = user.Id,
            Role = role,
            CanCreateOrganizations = request.CanCreateOrganizations ?? role is OrgRole.Owner or OrgRole.Admin,
            InvitedByUserId = http.User.UserId()
        });
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/organizations/current/members/{user.Id}", new MemberDto(
            user.Id, user.DisplayName, user.Email, role.ToString(), user.IdentityProvider,
            request.CanCreateOrganizations ?? role is OrgRole.Owner or OrgRole.Admin, [], DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> UpdateMemberAsync(
        Guid userId, [FromBody] UpdateMemberRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        if (http.User.Role() != OrgRole.Owner)
            return Results.Json(new ApiError("owner_only", "Only the organization owner can change levels."), statusCode: 403);

        var member = await db.OrganizationMembers
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.UserId == userId, ct);
        if (member is null) return Results.NotFound(new ApiError("not_found", "That member is not in this organization."));

        if (request.Role is not null)
        {
            if (!Enum.TryParse<OrgRole>(request.Role, ignoreCase: true, out var role))
                return Results.Json(new ApiError("invalid_role", "Role must be Member, Admin or Owner."), statusCode: 400);

            // Never let the last owner demote themselves out of the organization.
            if (member.Role == OrgRole.Owner && role != OrgRole.Owner)
            {
                var owners = await db.OrganizationMembers
                    .CountAsync(x => x.OrganizationId == orgId && x.Role == OrgRole.Owner, ct);
                if (owners <= 1)
                    return Results.Json(new ApiError("last_owner",
                        "Promote another member to Owner before changing this one."), statusCode: 409);
            }
            member.Role = role;
        }

        if (request.CanCreateOrganizations is not null)
            member.CanCreateOrganizations = request.CanCreateOrganizations.Value;

        await db.SaveChangesAsync(ct);
        return Results.Ok(new MemberDto(member.UserId, member.User.DisplayName, member.User.Email,
            member.Role.ToString(), member.User.IdentityProvider, member.CanCreateOrganizations, [], member.JoinedAt));
    }

    private static async Task<IResult> RemoveMemberAsync(Guid userId, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        if (http.User.Role() != OrgRole.Owner)
            return Results.Json(new ApiError("owner_only", "Only the organization owner can remove members."), statusCode: 403);

        var member = await db.OrganizationMembers
            .FirstOrDefaultAsync(x => x.OrganizationId == orgId && x.UserId == userId, ct);
        if (member is null) return Results.NoContent();

        if (member.Role == OrgRole.Owner &&
            await db.OrganizationMembers.CountAsync(x => x.OrganizationId == orgId && x.Role == OrgRole.Owner, ct) <= 1)
            return Results.Json(new ApiError("last_owner", "An organization must keep at least one owner."), statusCode: 409);

        db.OrganizationMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string DeriveName(string email) =>
        string.Join(' ', email.Split('@')[0].Split('.', '_', '-')
            .Where(x => x.Length > 0)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));

    private static async Task<string> UniqueSlugAsync(DochubDbContext db, string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        var suffix = 2;
        while (await db.Organizations.AnyAsync(x => x.Slug == slug, ct))
            slug = $"{baseSlug}-{suffix++}";
        return slug;
    }
}
