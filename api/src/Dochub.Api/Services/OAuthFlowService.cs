using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dochub.Api.Domain;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Services;

public record AuthorizeRequest(string Url, string State, string RedirectUri);

public record OAuthTokens(
    string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt, string? Scopes, string? Account);

/// <summary>
/// Runs the delegated OAuth dance that lets a user point Dochub at their own
/// Drive, SharePoint or private GitHub repositories.
///
/// Authorization Code with PKCE, and the code is exchanged **server-side** so the
/// refresh token never reaches the browser. That refresh token is what makes a
/// recurring sync survive past the hour an access token lasts.
/// </summary>
public interface IOAuthFlowService
{
    bool IsConfigured(SourceProvider provider);
    AuthorizeRequest BuildAuthorizeRequest(SourceProvider provider, Guid organizationId, Guid userId, SourceType sourceType);
    Task<(OAuthTokens Tokens, Guid OrganizationId, Guid UserId, SourceType SourceType)>
        CompleteAsync(string code, string state, CancellationToken ct);
    Task<OAuthTokens> RefreshAsync(SourceProvider provider, string refreshToken, CancellationToken ct);
}

public class OAuthFlowService(
    IOptions<OAuthOptions> options,
    IDataProtectionProvider protection,
    IHttpClientFactory http,
    ILogger<OAuthFlowService> log) : IOAuthFlowService
{
    /// <summary>The window a user has to finish consenting before the state expires.</summary>
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(15);

    private readonly OAuthOptions _options = options.Value;
    private readonly IDataProtector _protector = protection.CreateProtector("Dochub.OAuth.State.v1");

    public bool IsConfigured(SourceProvider provider) => _options.For(provider).IsConfigured;

    public AuthorizeRequest BuildAuthorizeRequest(
        SourceProvider provider, Guid organizationId, Guid userId, SourceType sourceType)
    {
        var settings = _options.For(provider);
        if (!settings.IsConfigured)
            throw new InvalidOperationException(
                $"OAuth:{provider}:ClientId is not configured, so Dochub cannot ask {provider} for access.");

        // A blank redirect URI is dropped from the query string rather than sent
        // empty, and the provider then reports a missing reply address — Microsoft
        // says AADSTS900971 — which gives no hint that the fault is local config.
        RequireRedirectUri(_options.RedirectUri);

        // PKCE: the verifier stays on our side, only its hash goes to the provider.
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        // The state is self-contained and encrypted, so no server-side store is
        // needed and a callback cannot be replayed into another user's account.
        var state = _protector.Protect(JsonSerializer.Serialize(new StatePayload(
            provider, organizationId, userId, sourceType, verifier, DateTimeOffset.UtcNow.Add(StateLifetime))));

        var query = new Dictionary<string, string?>
        {
            ["client_id"] = settings.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _options.RedirectUri,
            ["scope"] = settings.Scopes,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };

        if (provider == SourceProvider.Google)
        {
            // Google only returns a refresh token when both of these are present,
            // and only on a consent screen the user actually sees.
            query["access_type"] = "offline";
            query["prompt"] = "consent";
            query["include_granted_scopes"] = "true";
        }

        // AddQueryString skips null values, so anything required is asserted above
        // rather than trusted to be present.
        var url = QueryHelpers(settings.ResolvedAuthorizeEndpoint, query);
        return new AuthorizeRequest(url, state, _options.RedirectUri);
    }

    public async Task<(OAuthTokens, Guid, Guid, SourceType)> CompleteAsync(string code, string state, CancellationToken ct)
    {
        StatePayload payload;
        try
        {
            payload = JsonSerializer.Deserialize<StatePayload>(_protector.Unprotect(state))
                ?? throw new InvalidOperationException("The sign-in state could not be read.");
        }
        catch (CryptographicException)
        {
            // Tampered, or minted by a different instance's key ring.
            throw new InvalidOperationException("That sign-in link is not valid. Start the connection again.");
        }

        if (payload.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("That sign-in took too long and expired. Start the connection again.");

        var settings = _options.For(payload.Provider);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = _options.RedirectUri,
            ["code_verifier"] = payload.CodeVerifier
        };
        if (!string.IsNullOrWhiteSpace(settings.ClientSecret))
            form["client_secret"] = settings.ClientSecret;

        var tokens = await PostTokenRequestAsync(settings, form, ct);
        log.LogInformation("Connected {Provider} for user {UserId} in org {OrgId}",
            payload.Provider, payload.UserId, payload.OrganizationId);

        return (tokens, payload.OrganizationId, payload.UserId, payload.SourceType);
    }

    /// <summary>
    /// Validates the configured reply address. Exposed so startup can fail on a
    /// bad value instead of leaving it to the first person who clicks sign in.
    /// </summary>
    public static void RequireRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
            throw new InvalidOperationException(
                "OAuth:RedirectUri is not set. It must be the web app's callback address, " +
                "registered verbatim with the provider — for example http://localhost:5173/oauth/callback.");

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(
                $"OAuth:RedirectUri ('{redirectUri}') is not an absolute http or https URL. " +
                "Providers reject anything else.");

        if (!string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"OAuth:RedirectUri ('{redirectUri}') must not contain a '#' fragment.");
    }

    public async Task<OAuthTokens> RefreshAsync(SourceProvider provider, string refreshToken, CancellationToken ct)
    {
        var settings = _options.For(provider);
        var form = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId!,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken
        };
        if (!string.IsNullOrWhiteSpace(settings.ClientSecret))
            form["client_secret"] = settings.ClientSecret;
        if (provider == SourceProvider.Microsoft)
            form["scope"] = settings.Scopes;

        return await PostTokenRequestAsync(settings, form, ct);
    }

    private async Task<OAuthTokens> PostTokenRequestAsync(
        OAuthProviderOptions settings, Dictionary<string, string> form, CancellationToken ct)
    {
        var client = http.CreateClient("oauth");
        using var request = new HttpRequestMessage(HttpMethod.Post, settings.ResolvedTokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        // GitHub answers form-encoded unless JSON is asked for.
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        // GitHub reports a bad code with 200 and an error body.
        if (!response.IsSuccessStatusCode || TryReadError(body) is not null)
        {
            // The provider's own error is far more useful than a generic failure.
            var detail = TryReadError(body) ?? $"{(int)response.StatusCode} {response.StatusCode}";
            throw new InvalidOperationException($"The provider rejected the sign-in: {detail}");
        }

        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;

        var accessToken = root.TryGetProperty("access_token", out var at) ? at.GetString() : null;
        if (string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("The provider returned no access token.");

        // A GitHub OAuth App token does not expire, and says so by sending no expires_in.
        DateTimeOffset? expiresAt = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var seconds)
            ? DateTimeOffset.UtcNow.AddSeconds(seconds)
            : null;

        return new OAuthTokens(
            accessToken,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            expiresAt,
            root.TryGetProperty("scope", out var sc) ? sc.GetString() : settings.Scopes,
            ReadAccount(root));
    }

    /// <summary>Best-effort display name for the connected account, from the id token if present.</summary>
    private static string? ReadAccount(JsonElement root)
    {
        if (!root.TryGetProperty("id_token", out var idToken)) return null;
        var parts = (idToken.GetString() ?? "").Split('.');
        if (parts.Length < 2) return null;

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var name in new[] { "email", "preferred_username", "upn", "name" })
                if (claims.RootElement.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text)
                    return text;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            // A label is a nicety; never fail a working connection over it.
        }
        return null;
    }

    private static string? TryReadError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var code = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (code is null) return null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            return description is null ? code : $"{code} — {description}";
        }
        catch (JsonException) { return null; }
    }

    private static string QueryHelpers(string url, Dictionary<string, string?> query) =>
        Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(url, query);

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private record StatePayload(
        SourceProvider Provider, Guid OrganizationId, Guid UserId,
        SourceType SourceType, string CodeVerifier, DateTimeOffset ExpiresAt);
}
