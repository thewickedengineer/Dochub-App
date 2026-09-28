using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Dochub.Api.Auth;

/// <summary>Mints the API's own bearer token once an SSO identity has been verified.</summary>
public interface IAccessTokenService
{
    (string Token, DateTimeOffset ExpiresAt) Issue(User user, Organization? organization, OrgRole? role);
}

public class AccessTokenService(IOptions<JwtOptions> options) : IAccessTokenService
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) Issue(User user, Organization? organization, OrgRole? role)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Name, user.DisplayName),
            new("idp", user.IdentityProvider),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("n"))
        };

        // The active org is baked into the token: every request is tenant-scoped
        // by the claim, never by a client-supplied header.
        if (organization is not null)
        {
            claims.Add(new Claim("org_id", organization.Id.ToString()));
            claims.Add(new Claim("org_name", organization.Name));
        }
        if (role is not null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role.Value.ToString()));
            claims.Add(new Claim("org_role", role.Value.ToString()));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}

public static class PrincipalExtensions
{
    public static Guid UserId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)
            ? id
            : throw new UnauthorizedAccessException("Token has no subject.");

    public static Guid? OrganizationId(this ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirst("org_id")?.Value, out var id) ? id : null;

    public static Guid RequireOrganizationId(this ClaimsPrincipal principal) =>
        principal.OrganizationId() ?? throw new UnauthorizedAccessException(
            "No organization selected. Call POST /api/auth/switch-organization first.");

    public static OrgRole Role(this ClaimsPrincipal principal) =>
        Enum.TryParse<OrgRole>(principal.FindFirst("org_role")?.Value, out var role) ? role : OrgRole.Member;
}
