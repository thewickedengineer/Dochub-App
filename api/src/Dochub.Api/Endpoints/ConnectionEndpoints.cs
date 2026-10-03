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

        group.MapGet("/options", Options)
            .WithSummary("The sources that can be connected, and whether each signs in through a window or takes a token");

        group.MapGet("/oauth/{sourceType}/start", StartOAuthAsync)
            .WithSummary("Begin signing in to Google, Microsoft or GitHub to grant access to documents")
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

        group.MapGet("/{connectionId:guid}/browse", BrowseAsync)
            .WithSummary("List the places, sites, folders and files a connected account can see")
            .WithDescription("Backs the SharePoint / OneDrive and Google Drive browser. 'location' is the opaque value " +
                             "from a previous row (empty for the top level), 'q' searches, and 'cursor' fetches the next page.");

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

        var ids = connections.Select(x => (Guid?)x.Id).ToList();
        var schedules = await db.RecurringSyncSchedules
            .Where(x => ids.Contains(x.SourceConnectionId))
            .GroupBy(x => x.SourceConnectionId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        // Expiry is reported rather than stored-and-forgotten, so the UI can
        // prompt for a reconnect before an import fails mid-flight.
        return Results.Ok(connections.Select(c => ToDto(c) with { ScheduleCount = schedules.GetValueOrDefault(c.Id) }));
    }

    /// <summary>Sources with a working connector. Confluence and Jira have none yet.</summary>
    private static readonly SourceType[] Connectable =
        [SourceType.GitHub, SourceType.SharePoint, SourceType.GoogleDrive, SourceType.AzureDevOps];

    private static IResult Options(IOAuthFlowService oauth) => Results.Ok(Connectable.Select(type =>
    {
        var signIn = SourceTokenProvider.SupportsExternalSignIn(type);
        var configured = signIn && oauth.IsConfigured(SourceTokenProvider.ProviderFor(type));
        // GitHub also takes a personal access token, for when no OAuth App is registered.
        return new ConnectionOptionDto(type.ToString(), type.Label(), signIn, configured,
            AcceptsToken: type == SourceType.GitHub || !signIn);
    }));

    private static async Task<IResult> ConnectAsync(
        [FromBody] ConnectSourceRequest request, HttpContext http,
        DochubDbContext db, ITokenProtector protector, IHttpClientFactory httpClients, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        SourceType sourceType;
        try { sourceType = Mapping.ParseSource(request.SourceType); }
        catch (ArgumentException ex) { return Results.Json(new ApiError("invalid_source", ex.Message), statusCode: 400); }

        if (string.IsNullOrWhiteSpace(request.AccessToken))
            return Results.Json(new ApiError("missing_token", "An access token is required."), statusCode: 400);

        // A GitHub token is checked with GitHub before it is kept, so a typo shows up
        // now and the connection can say which account it reads as.
        string? account = null;
        if (sourceType == SourceType.GitHub)
        {
            var check = await GitHubAccountAsync(httpClients, request.AccessToken.Trim(), ct);
            if (check.Error is not null)
                return Results.Json(new ApiError("invalid_token", check.Error), statusCode: 400);
            account = check.Login;
        }

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
        connection.Account = account ?? connection.Account;
        connection.ProtectedAccessToken = protector.Protect(request.AccessToken.Trim());
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

    private static async Task<(string? Login, string? Error)> GitHubAccountAsync(
        IHttpClientFactory httpClients, string token, CancellationToken ct)
    {
        var client = httpClients.CreateClient("github");
        using var request = new HttpRequestMessage(HttpMethod.Get, "user");
        request.Headers.Authorization = new("Bearer", token);
        request.Headers.Accept.Clear();
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        try
        {
            using var response = await client.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized)
                return (null, "GitHub rejected that token. Check it was copied whole and hasn't expired.");
            if (!response.IsSuccessStatusCode)
                return (null, $"GitHub answered {(int)response.StatusCode} when checking the token.");
            using var body = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return (body.RootElement.TryGetProperty("login", out var login) ? login.GetString() : null, null);
        }
        catch (HttpRequestException e)
        {
            return (null, $"Could not reach GitHub to check the token: {e.Message}");
        }
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
                $"{parsed.Label()} does not have a sign-in window; connect it with a token."), statusCode: 400);

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
        DochubDbContext db, IOAuthFlowService oauth, ITokenProtector protector,
        IHttpClientFactory httpClients, CancellationToken ct)
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

        // GitHub sends no id token; ask it who signed in.
        var account = tokens.Account;
        if (sourceType == SourceType.GitHub && account is null)
            account = (await GitHubAccountAsync(httpClients, tokens.AccessToken, ct)).Login;

        connection.DisplayName = sourceType.Label();
        connection.Account = account;
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

    private static async Task<IResult> BrowseAsync(
        Guid connectionId, string? location, string? q, string? cursor, HttpContext http, DochubDbContext db,
        ISourceBrowser browser, ISourceTokenProvider tokens, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        var connection = await db.SourceConnections.FirstOrDefaultAsync(
            x => x.Id == connectionId && x.OrganizationId == orgId && x.UserId == userId, ct);
        if (connection is null || connection.Status == ConnectionStatus.Revoked)
            return Results.Json(new ApiError("connection_not_found", "Sign in to the source first."), statusCode: 404);

        try
        {
            var token = await tokens.GetAccessTokenAsync(connection, ct);
            return Results.Ok(await browser.BrowseAsync(connection.SourceType, token, location, q, cursor, ct));
        }
        catch (ArgumentException ex)
        {
            return Results.Json(new ApiError("invalid_location", ex.Message), statusCode: 400);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new ApiError("browse_failed", ex.Message), statusCode: 409);
        }
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
        Guid connectionId, HttpContext http, DochubDbContext db, ITokenProtector protector,
        IHttpClientFactory httpClients, Microsoft.Extensions.Options.IOptions<OAuthOptions> oauthOptions,
        ILoggerFactory logs, CancellationToken ct)
    {
        var orgId = http.User.RequireOrganizationId();
        var userId = http.User.UserId();

        var connection = await db.SourceConnections.FirstOrDefaultAsync(
            x => x.Id == connectionId && x.OrganizationId == orgId && x.UserId == userId, ct);
        if (connection is null) return Results.NoContent();

        // A GitHub sign-in is also withdrawn at GitHub, so Dochub drops off the
        // user's authorized apps. Best effort: the token is gone from here either way.
        var github = oauthOptions.Value.GitHub;
        if (connection.SourceType == SourceType.GitHub && connection.ProtectedAccessToken is not null
            && github.IsConfigured && !string.IsNullOrWhiteSpace(github.ClientSecret) && connection.ExpiresAt is null
            && connection.Scopes?.Contains("repo") == true)
        {
            try
            {
                var client = httpClients.CreateClient("github");
                using var revoke = new HttpRequestMessage(HttpMethod.Delete, $"applications/{github.ClientId}/grant")
                {
                    Content = JsonContent.Create(new { access_token = protector.Unprotect(connection.ProtectedAccessToken) })
                };
                revoke.Headers.Authorization = new("Basic",
                    Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{github.ClientId}:{github.ClientSecret}")));
                using var response = await client.SendAsync(revoke, ct);
                if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                    logs.CreateLogger("Connections").LogWarning("GitHub did not revoke the grant: {Status}", (int)response.StatusCode);
            }
            catch (HttpRequestException e)
            {
                logs.CreateLogger("Connections").LogWarning(e, "Could not reach GitHub to revoke the grant");
            }
        }

        // Kept as a revoked row so in-flight batches report a clear reason.
        connection.Status = ConnectionStatus.Revoked;
        connection.ProtectedAccessToken = null;
        connection.ProtectedRefreshToken = null;
        connection.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.NoContent();
    }
}
