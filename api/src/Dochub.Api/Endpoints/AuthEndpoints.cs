using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/sso", SignInAsync)
            .AllowAnonymous()
            .WithSummary("Exchange a Google or Microsoft ID token for a Dochub access token")
            .WithDescription("New users are created on first sign-in but hold no organization until an owner adds them.");

        group.MapPost("/switch-organization", SwitchOrganizationAsync)
            .RequireAuthorization()
            .WithSummary("Re-issue the access token scoped to another organization");

        group.MapGet("/me", MeAsync)
            .RequireAuthorization()
            .WithSummary("Current user, active organization and memberships");
    }

    private static async Task<IResult> SignInAsync(
        [FromBody] SsoSignInRequest request, ISsoValidator validator,
        IAccessTokenService tokens, DochubDbContext db, CancellationToken ct)
    {
        ExternalIdentity identity;
        try
        {
            identity = await validator.ValidateAsync(request.Provider, request.IdToken, ct);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return Results.Json(new ApiError("sso_rejected", ex.Message), statusCode: StatusCodes.Status401Unauthorized);
        }

        var email = identity.Email.ToLowerInvariant();
        var user = await db.Users.FirstOrDefaultAsync(
            x => x.Email == email || (x.IdentityProvider == identity.Provider && x.ExternalSubject == identity.Subject), ct);

        if (user is null)
        {
            user = new User
            {
                Email = email,
                DisplayName = identity.DisplayName,
                AvatarUrl = identity.AvatarUrl,
                IdentityProvider = identity.Provider,
                ExternalSubject = identity.Subject
            };
            db.Users.Add(user);
        }
        else
        {
            // Keep the profile fresh, and let a user who first appeared via an
            // owner's invite bind to whichever provider they actually signed in with.
            user.DisplayName = identity.DisplayName;
            user.AvatarUrl = identity.AvatarUrl ?? user.AvatarUrl;
            user.IdentityProvider = identity.Provider;
            user.ExternalSubject = identity.Subject;
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return await IssueAsync(db, tokens, user, request.OrganizationId, ct);
    }

    private static async Task<IResult> SwitchOrganizationAsync(
        [FromBody] SwitchOrganizationRequest request, HttpContext http,
        IAccessTokenService tokens, DochubDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return Results.Unauthorized();

        var isMember = await db.OrganizationMembers
            .AnyAsync(x => x.UserId == userId && x.OrganizationId == request.OrganizationId, ct);
        if (!isMember)
            return Results.Json(new ApiError("not_a_member", "You are not a member of that organization."),
                statusCode: StatusCodes.Status403Forbidden);

        return await IssueAsync(db, tokens, user, request.OrganizationId, ct);
    }

    private static async Task<IResult> MeAsync(HttpContext http, IAccessTokenService tokens, DochubDbContext db, CancellationToken ct)
    {
        var userId = http.User.UserId();
        var user = await db.Users.FirstOrDefaultAsync(x => x.Id == userId, ct);
        if (user is null) return Results.Unauthorized();
        return await IssueAsync(db, tokens, user, http.User.OrganizationId(), ct);
    }

    /// <summary>
    /// Builds the auth payload, defaulting the active organization to the
    /// requested one, else the user's first membership.
    /// </summary>
    private static async Task<IResult> IssueAsync(
        DochubDbContext db, IAccessTokenService tokens, User user, Guid? organizationId, CancellationToken ct)
    {
        var memberships = await db.OrganizationMembers
            .Where(x => x.UserId == user.Id)
            .Include(x => x.Organization)
            .OrderBy(x => x.Organization.Name)
            .ToListAsync(ct);

        var counts = await db.OrganizationMembers
            .Where(x => memberships.Select(m => m.OrganizationId).Contains(x.OrganizationId))
            .GroupBy(x => x.OrganizationId)
            .Select(g => new { OrganizationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.OrganizationId, x => x.Count, ct);

        var organizations = memberships.Select(m => new OrganizationDto(
            m.Organization.Id, m.Organization.Name, m.Organization.Slug, m.Organization.Initials,
            m.Organization.Plan, m.Role.ToString(), counts.GetValueOrDefault(m.OrganizationId))).ToList();

        var active = memberships.FirstOrDefault(m => m.OrganizationId == organizationId) ?? memberships.FirstOrDefault();
        var activeDto = active is null ? null : organizations.First(o => o.Id == active.OrganizationId);

        var (token, expiresAt) = tokens.Issue(user, active?.Organization, active?.Role);

        var canCreateOrgs = user.CanCreateOrganizations
            || memberships.Any(m => m.CanCreateOrganizations || m.Role is OrgRole.Owner or OrgRole.Admin);

        var userDto = new UserDto(user.Id, user.Email, user.DisplayName, user.AvatarUrl,
            user.IdentityProvider, canCreateOrgs, active?.Role.ToString());

        return Results.Ok(new AuthResponse(token, expiresAt, userDto, activeDto, organizations));
    }
}
