using System.IdentityModel.Tokens.Jwt;
using Google.Apis.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Dochub.Api.Auth;

using Dochub.Api.Services;

public record ExternalIdentity(string Provider, string Subject, string Email, string DisplayName, string? AvatarUrl);

/// <summary>Verifies an ID token issued by Google or Microsoft Entra ID.</summary>
public interface ISsoValidator
{
    Task<ExternalIdentity> ValidateAsync(string provider, string idToken, CancellationToken ct);
}

public class SsoValidator(
    IOptions<SsoOptions> options,
    IOptions<OAuthOptions> oauth,
    ILogger<SsoValidator> log) : ISsoValidator
{
    // One app registration is the common case, so an unset Sso value falls back
    // to the OAuth one rather than making the same id be configured twice.
    private readonly SsoOptions _options = options.Value.ResolvedAgainst(oauth.Value);
    private ConfigurationManager<OpenIdConnectConfiguration>? _microsoftConfig;

    public Task<ExternalIdentity> ValidateAsync(string provider, string idToken, CancellationToken ct) =>
        provider.ToLowerInvariant() switch
        {
            "google" => ValidateGoogleAsync(idToken, ct),
            "microsoft" => ValidateMicrosoftAsync(idToken, ct),
            "dev" when _options.AllowDevSignIn => Task.FromResult(ParseDevIdentity(idToken)),
            _ => throw new UnauthorizedAccessException($"Unsupported identity provider '{provider}'.")
        };

    private async Task<ExternalIdentity> ValidateGoogleAsync(string idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.GoogleClientId))
            throw new InvalidOperationException(
                "No Google client id is configured. Set Sso:GoogleClientId, or OAuth:Google:ClientId " +
                "if one registration serves both. It must be the same id the browser used, " +
                "since it is what the token's audience is checked against.");

        var settings = new GoogleJsonWebSignature.ValidationSettings
        {
            Audience = [_options.GoogleClientId]
        };
        var payload = await GoogleJsonWebSignature.ValidateAsync(idToken, settings);

        if (!payload.EmailVerified)
            throw new UnauthorizedAccessException("Google account email is not verified.");

        return new ExternalIdentity("google", payload.Subject, payload.Email,
            payload.Name ?? payload.Email, payload.Picture);
    }

    private async Task<ExternalIdentity> ValidateMicrosoftAsync(string idToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_options.MicrosoftClientId))
            throw new InvalidOperationException(
                "No Microsoft client id is configured. Set Sso:MicrosoftClientId, or OAuth:Microsoft:ClientId " +
                "if one registration serves both. It must be the same id VITE_MICROSOFT_CLIENT_ID uses, " +
                "since it is what the token's audience is checked against.");

        _microsoftConfig ??= new ConfigurationManager<OpenIdConnectConfiguration>(
            $"https://login.microsoftonline.com/{_options.MicrosoftTenant}/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

        var config = await _microsoftConfig.GetConfigurationAsync(ct);
        var handler = new JwtSecurityTokenHandler();

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // The "common" endpoint issues per-tenant issuers, so match the template.
            IssuerValidator = (issuer, _, _) =>
                issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal)
                    ? issuer
                    : throw new SecurityTokenInvalidIssuerException($"Unexpected issuer '{issuer}'."),
            ValidateAudience = true,
            ValidAudience = _options.MicrosoftClientId,
            ValidateLifetime = true,
            IssuerSigningKeys = config.SigningKeys,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        var principal = handler.ValidateToken(idToken, parameters, out _);

        var subject = principal.FindFirst("oid")?.Value
            ?? principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? throw new UnauthorizedAccessException("Microsoft token is missing a subject claim.");
        var email = principal.FindFirst("preferred_username")?.Value
            ?? principal.FindFirst("email")?.Value
            ?? throw new UnauthorizedAccessException("Microsoft token is missing an email claim.");
        var name = principal.FindFirst("name")?.Value ?? email;

        return new ExternalIdentity("microsoft", subject, email, name, null);
    }

    /// <summary>
    /// Development-only path: the body carries "email|Display Name" so the UI can be
    /// exercised before real OAuth apps exist. Gated on Sso:AllowDevSignIn.
    /// </summary>
    private ExternalIdentity ParseDevIdentity(string value)
    {
        log.LogWarning("Dev sign-in used — never enable Sso:AllowDevSignIn outside local development.");
        var parts = value.Split('|', 2);
        var email = parts[0].Trim();
        if (!email.Contains('@')) throw new UnauthorizedAccessException("Dev sign-in requires an email address.");
        var name = parts.Length > 1 && parts[1].Length > 0
            ? parts[1].Trim()
            : string.Join(' ', email.Split('@')[0].Split('.', '_')
                .Select(x => x.Length == 0 ? x : char.ToUpperInvariant(x[0]) + x[1..]));
        return new ExternalIdentity("dev", email.ToLowerInvariant(), email.ToLowerInvariant(), name, null);
    }
}
