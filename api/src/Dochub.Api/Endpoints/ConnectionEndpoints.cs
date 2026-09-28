using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Endpoints;

/// <summary>
/// Token grants for source systems. The UI's "Sign in with Microsoft/Google"
/// buttons complete OAuth in the browser and post the resulting access token here.
/// </summary>
public static class ConnectionEndpoints
{
    public static void MapConnectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/connections").WithTags("Connections").RequireAuthorization();

        group.MapGet("/", ListAsync).WithSummary("Source connections for the caller in the active organization");

        group.MapGet("/oauth/{sourceType}/start", StartOAuthAsync)
            .WithSummary("Begin signing in to Google or Microsoft to grant access to documents")
            .WithDescription("""
                Returns the provider's consent URL. The browser opens it in a popup, so the
                page keeping the user's staged sources is never navigated away from. The
                state is encrypted and self-contained, so no server-side session is needed.
                """);

        group.MapPost("/oauth/callback", CompleteOAuthAsync)
            .WithSummary("Exchange the authorization code for tokens and store the connection")
            .WithDescription("""
                Posted by the popup after the provider redirects back. The code is exchanged
                server-side so the refresh token never reaches the browser — that refresh
                token is what keeps scheduled syncs working beyond the access token's hour.
                """);

        group.MapPost("/resolve-link", ResolveLinkAsync)
            .WithSummary("Turn a pasted Drive or SharePoint link into the ids a connector needs");

        group.MapPost("/", ConnectAsync)
            .WithSummary("Store a source access token directly")
            .WithDescription("An escape hatch for tokens obtained elsewhere. Prefer the OAuth flow, which also yields a refresh token.");

        group.MapDelete("/{connectionId:guid}", DisconnectAsync).WithSummary("Revoke a source connection");
    }

    private static async Task<IResult> ListAsync(HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        var connections = await db.SourceConnections
            .Where(x => x.OrganizationId == orgId && x.UserId == userId)
            .OrderBy(x => x.SourceType).AsNoTracking().ToListAsync(ct);

        // Expiry is reported rather than stored-and-forgotten, so the UI can
        // prompt for a reconnect before an import fails mid-flight.
        return Results.Ok(connections.Select(ToDto));
    }

    private static async Task<IResult> ConnectAsync(
        [FromBody] ConnectSourceRequest request, HttpContext http,
        DochubDbContext db, ITokenProtector protector, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        SourceType sourceType;
        try { sourceType = Mapping.ParseSource(request.SourceType); }
        catch (ArgumentException ex) { return Results.Json(new ApiError("invalid_source", ex.Message), statusCode: 400); }

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Results.Json(new ApiError("missing_token", "An access token is required."), statusCode: 400);

        var connection = await db.SourceConnections.FirstOrDefaultAsync(
            x => x.OrganizationId == orgId && x.UserId == userId && x.SourceType == sourceType, ct);

        if (connection is null)
        {
            connection = new SourceConnection
            {
                OrganizationId = orgId,
                UserId = userId,
                SourceType = sourceType
            };
            db.SourceConnections.Add(connection);
        }

        connection.DisplayName = request.DisplayName?.Trim() is { Length: > 0 } name ? name : sourceType.Label();
        connection.ProtectedAccessToken = protector.Protect(request.AccessToken);
        connection.ProtectedRefreshToken = string.IsNullOrWhiteSpace(request.RefreshToken)
            ? connection.ProtectedRefreshToken
            : protector.Protect(request.RefreshToken);
        connection.ExpiresAt = request.ExpiresAt;
        connection.Scopes = request.Scopes;
        connection.Status = ConnectionStatus.Connected;
        connection.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(connection));
    }

    private static async Task<IResult> StartOAuthAsync(
        string sourceType, HttpContext http, IOAuthFlowService oauth, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        SourceType parsed;
        try { parsed = Mapping.ParseSource(sourceType); }
        catch (ArgumentException ex) { return Results.Json(new ApiError("invalid_source", ex.Message), statusCode: 400); }

        if (!SourceTokenProvider.SupportsExternalSignIn(parsed))
            return Results.Json(new ApiError("not_supported",
                $"{parsed.Label()} does not sign in through Google or Microsoft."), statusCode: 400);

        var provider = SourceTokenProvider.ProviderFor(parsed);
        if (!oauth.IsConfigured(provider))
            return Results.Json(new ApiError("not_configured",
                $"{provider} sign-in is not set up. Register an OAuth app and set OAuth:{provider}:ClientId."),
                statusCode: 503);

        try
        {
            var request = oauth.BuildAuthorizeRequest(provider, orgId, userId, parsed);
            return Results.Ok(new StartOAuthResponse(
                request.Url, provider.ToString(), parsed.ToString(), request.RedirectUri));
        }
        catch (InvalidOperationException ex)
        {
            // Configuration is wrong, not the request — say which setting.
            return Results.Json(new ApiError("not_configured", ex.Message), statusCode: 503);
        }
    }

    private static async Task<IResult> CompleteOAuthAsync(
        [FromBody] CompleteOAuthRequest request, HttpContext http,
        DochubDbContext db, IOAuthFlowService oauth, ITokenProtector protector, CancellationToken ct)
    {
        var callerOrgId = http.User.RequireOrganizationId();
        var callerUserId = http.User.UserId();

        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.State))
            return Results.Json(new ApiError("invalid_callback", "The provider returned no code."), statusCode: 400);

        OAuthTokens tokens;
        Guid organizationId, userId;
        SourceType sourceType;
        try
        {
            (tokens, organizationId, userId, sourceType) = await oauth.CompleteAsync(request.Code, request.State, ct);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new ApiError("oauth_failed", ex.Message), statusCode: 400);
        }

        // The state says who started the flow. If that is not who came back, the
        // callback was replayed into someone else's session — refuse it.
        if (organizationId != callerOrgId || userId != callerUserId)
            return Results.Json(new ApiError("state_mismatch",
                "That sign-in was started by a different user or organization."), statusCode: 403);

        var connection = await db.SourceConnections.FirstOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.UserId == userId && x.SourceType == sourceType, ct);

        if (connection is null)
        {
            connection = new SourceConnection
            {
                OrganizationId = organizationId,
                UserId = userId,
                SourceType = sourceType
            };
            db.SourceConnections.Add(connection);
        }

        connection.DisplayName = sourceType.Label();
        connection.Account = tokens.Account;
        connection.ProtectedAccessToken = protector.Protect(tokens.AccessToken);
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            connection.ProtectedRefreshToken = protector.Protect(tokens.RefreshToken);
        connection.ExpiresAt = tokens.ExpiresAt;
        connection.Scopes = tokens.Scopes;
        connection.Status = ConnectionStatus.Connected;
        connection.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(connection));
    }

    private static async Task<IResult> ResolveLinkAsync(
        [FromBody] ResolveLinkRequest request, HttpContext http, DochubDbContext db,
        IShareLinkResolver resolver, ISourceTokenProvider tokens, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        var connection = await db.SourceConnections.FirstOrDefaultAsync(
            x => x.Id == request.SourceConnectionId && x.OrganizationId == orgId && x.UserId == userId, ct);
        if (connection is null)
            return Results.Json(new ApiError("connection_not_found",
                "Sign in to the source before pasting a link — resolving it needs that account's access."),
                statusCode: 400);

        try
        {
            var token = await tokens.GetAccessTokenAsync(connection, ct);
            var resolved = await resolver.ResolveAsync(request.Link, token, ct);
            return Results.Ok(new ResolvedLinkDto(
                resolved.SourceType.ToString(), resolved.DisplayName,
                resolved.SourceReference, resolved.Options));
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new ApiError("invalid_link", ex.Message), statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new ApiError("link_unresolved", ex.Message), statusCode: 409);
        }
    }

    private static SourceConnectionDto ToDto(SourceConnection c) => new(
        c.Id, c.SourceType.ToString(), c.DisplayName,
        c.ExpiresAt is { } e && e <= DateTimeOffset.UtcNow && c.ProtectedRefreshToken is null
            ? nameof(ConnectionStatus.Expired)
            : c.Status.ToString(),
        c.ExpiresAt, c.Scopes, c.UpdatedAt, c.Account,
        // A connection that can renew itself never needs the user back.
        c.ProtectedRefreshToken is not null);

    private static async Task<IResult> DisconnectAsync(
        Guid connectionId, HttpContext http, DochubDbContext db, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        var connection = await db.SourceConnections.FirstOrDefaultAsync(
            x => x.Id == connectionId && x.OrganizationId == orgId && x.UserId == userId, ct);
        if (connection is null) return Results.NoContent();

        // Kept as a revoked row so in-flight batches report a clear reason.
        connection.Status = ConnectionStatus.Revoked;
        connection.ProtectedAccessToken = null;
        connection.ProtectedRefreshToken = null;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
