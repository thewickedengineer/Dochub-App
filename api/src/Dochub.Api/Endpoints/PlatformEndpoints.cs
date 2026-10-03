using System.Net.Mail;
using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

/// <summary>
/// The Creator's view across every organization: create them, and assign or
/// replace their owners, without being a member. Only users listed in
/// Platform:Creators may call these; everyone else gets 403.
///
/// Owners are named by their login id — the address they sign in with, for a
/// Microsoft work/school or personal account or a Google account. Someone who has
/// never signed in gets a placeholder that binds to their real identity the first
/// time they sign in with that address.
/// </summary>
public static class PlatformEndpoints
{
    public static void MapPlatformEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/platform").WithTags("Platform").RequireAuthorization()
            .AddEndpointFilter(async (context, next) =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<DochubDbContext>();
                var userId = context.HttpContext.User.UserId();
                // Read from the database, not the token: removing someone from the list takes effect at once.
                var isCreator = await db.Users.AnyAsync(u => u.Id == userId && u.IsCreator);
                return isCreator
                    ? await next(context)
                    : Results.Json(new ApiError("creator_only", "Only a Creator can manage organizations across the platform."),
                        statusCode: StatusCodes.Status403Forbidden);
            });

        group.MapGet("/organizations", ListAsync).WithSummary("Every organization, with its owners");
        group.MapPost("/organizations/{id:guid}/owners", AddOwnerAsync)
            .WithSummary("Make someone an owner of an organization, by the login id they sign in with");
        group.MapDelete("/organizations/{id:guid}/owners/{userId:guid}", RemoveOwnerAsync)
            .WithSummary("Remove an owner (an organization always keeps at least one)");
    }

    public static bool IsLoginId(string value)
    {
        if (value.Length > 320 || value.Any(char.IsWhiteSpace)) return false;
        try { return new MailAddress(value).Address == value; }
        catch (FormatException) { return false; }
    }

    /// <summary>The user signing in with <paramref name="loginId"/>, created as a placeholder if they never have.</summary>
    public static async Task<User> FindOrInviteAsync(DochubDbContext db, string loginId, string? displayName, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == loginId, ct);
        if (user is not null) return user;
        user = new User
        {
            Email = loginId,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? DeriveName(loginId) : displayName.Trim(),
            IdentityProvider = "invited",
            ExternalSubject = $"invite:{Guid.NewGuid():n}"
        };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    private static async Task<IResult> ListAsync(DochubDbContext db, CancellationToken ct)
    {
        var organizations = await db.Organizations.AsNoTracking().OrderBy(o => o.Name).ToListAsync(ct);
        var members = await db.OrganizationMembers.AsNoTracking().Include(m => m.User).ToListAsync(ct);
        var creators = await db.Users.AsNoTracking()
            .Where(u => organizations.Select(o => o.CreatedByUserId).Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Email, ct);

        return Results.Ok(organizations.Select(o =>
        {
            var inOrg = members.Where(m => m.OrganizationId == o.Id).ToList();
            return new PlatformOrganizationDto(
                o.Id, o.Name, o.Slug, o.Initials, o.Plan, inOrg.Count,
                inOrg.Where(m => m.Role == OrgRole.Owner).OrderBy(m => m.User.Email).Select(ToOwner).ToList(),
                creators.GetValueOrDefault(o.CreatedByUserId), o.CreatedAt);
        }));
    }

    private static async Task<IResult> AddOwnerAsync(
        Guid id, AddOwnerRequest request, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        if (!await db.Organizations.AnyAsync(o => o.Id == id, ct))
            return Results.NotFound(new ApiError("not_found", "No such organization."));
        var loginId = request.LoginId?.Trim().ToLowerInvariant() ?? "";
        if (!IsLoginId(loginId))
            return Results.BadRequest(new ApiError("invalid_login_id",
                "Give the address the owner signs in with, e.g. name@company.com or name@outlook.com."));

        var user = await FindOrInviteAsync(db, loginId, request.DisplayName, ct);
        var membership = await db.OrganizationMembers.FirstOrDefaultAsync(m => m.OrganizationId == id && m.UserId == user.Id, ct);
        if (membership is null)
        {
            membership = new OrganizationMember { OrganizationId = id, UserId = user.Id, InvitedByUserId = http.User.UserId() };
            db.OrganizationMembers.Add(membership);
        }
        else if (membership.Role == OrgRole.Owner)
        {
            return Results.Conflict(new ApiError("already_owner", $"{loginId} is already an owner."));
        }
        // An existing member is promoted rather than added twice.
        membership.Role = OrgRole.Owner;
        membership.CanCreateOrganizations = true;
        await db.SaveChangesAsync(ct);

        return Results.Created($"/api/platform/organizations/{id}/owners/{user.Id}", ToOwner(user));
    }

    private static async Task<IResult> RemoveOwnerAsync(Guid id, Guid userId, DochubDbContext db, CancellationToken ct)
    {
        var membership = await db.OrganizationMembers
            .FirstOrDefaultAsync(m => m.OrganizationId == id && m.UserId == userId && m.Role == OrgRole.Owner, ct);
        if (membership is null) return Results.NotFound(new ApiError("not_found", "That person is not an owner here."));
        if (await db.OrganizationMembers.CountAsync(m => m.OrganizationId == id && m.Role == OrgRole.Owner, ct) <= 1)
            return Results.Conflict(new ApiError("last_owner", "Add another owner before removing this one."));

        // They stay in the organization as an Admin; removing them outright is the owners' decision.
        membership.Role = OrgRole.Admin;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static OwnerDto ToOwner(OrganizationMember m) => ToOwner(m.User);

    private static OwnerDto ToOwner(User u) =>
        new(u.Id, u.Email, u.DisplayName, u.IdentityProvider, u.IdentityProvider != "invited");

    private static string DeriveName(string email) =>
        string.Join(' ', email.Split('@')[0].Split('.', '_', '-')
            .Where(x => x.Length > 0)
            .Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
}
