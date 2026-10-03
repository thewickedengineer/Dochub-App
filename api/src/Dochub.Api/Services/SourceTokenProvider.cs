using Dochub.Api.Data;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Dochub.Api.Services;

/// <summary>
/// Hands out a usable access token for a source connection, refreshing it first
/// when it is close to expiry.
///
/// This is what lets a schedule set up today still run next month: the access
/// token lasts about an hour, the refresh token lasts until it is revoked.
/// </summary>
public interface ISourceTokenProvider
{
    Task<string> GetAccessTokenAsync(SourceConnection connection, CancellationToken ct);
}

public class SourceTokenProvider(
    DochubDbContext db,
    IOAuthFlowService oauth,
    ITokenProtector protector,
    ILogger<SourceTokenProvider> log) : ISourceTokenProvider
{
    /// <summary>Refresh this far ahead, so a long extraction cannot expire mid-run.</summary>
    private static readonly TimeSpan RenewAhead = TimeSpan.FromMinutes(5);

    public async Task<string> GetAccessTokenAsync(SourceConnection connection, CancellationToken ct)
    {
        if (connection.Status == ConnectionStatus.Revoked)
            throw new InvalidOperationException(
                $"The {connection.SourceType.Label()} connection was disconnected. Reconnect it to continue.");

        if (connection.ProtectedAccessToken is null)
            throw new InvalidOperationException(
                $"The {connection.SourceType.Label()} connection has no stored token. Reconnect it to continue.");

        var stillValid = connection.ExpiresAt is null
            || connection.ExpiresAt > DateTimeOffset.UtcNow.Add(RenewAhead);
        if (stillValid) return protector.Unprotect(connection.ProtectedAccessToken);

        if (connection.ProtectedRefreshToken is null)
        {
            // Nothing to renew with — usually a token that was pasted in rather
            // than granted through the consent flow.
            connection.Status = ConnectionStatus.Expired;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            throw new InvalidOperationException(
                $"The {connection.SourceType.Label()} token expired and there is no refresh token. " +
                "Reconnect the source to continue.");
        }

        var provider = ProviderFor(connection.SourceType);
        try
        {
            var refreshed = await oauth.RefreshAsync(
                provider, protector.Unprotect(connection.ProtectedRefreshToken), ct);

            connection.ProtectedAccessToken = protector.Protect(refreshed.AccessToken);
            // Providers may rotate the refresh token; keep the old one if they don't.
            if (!string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                connection.ProtectedRefreshToken = protector.Protect(refreshed.RefreshToken);
            connection.ExpiresAt = refreshed.ExpiresAt;
            connection.Status = ConnectionStatus.Connected;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            log.LogInformation("Refreshed the {Provider} token for connection {ConnectionId}", provider, connection.Id);
            return refreshed.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A refresh token is only rejected when access has genuinely gone:
            // revoked in the provider's console, password changed, admin removed it.
            connection.Status = ConnectionStatus.Expired;
            connection.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            log.LogWarning(ex, "Could not refresh connection {ConnectionId}", connection.Id);
            throw new InvalidOperationException(
                $"{connection.SourceType.Label()} would not renew the connection ({ex.Message}). " +
                "Reconnect the source to continue.", ex);
        }
    }

    public static SourceProvider ProviderFor(SourceType sourceType) => sourceType switch
    {
        SourceType.GoogleDrive => SourceProvider.Google,
        SourceType.SharePoint or SourceType.AzureDevOps => SourceProvider.Microsoft,
        SourceType.GitHub => SourceProvider.GitHub,
        _ => throw new NotSupportedException($"{sourceType} does not sign in through an external provider.")
    };

    public static bool SupportsExternalSignIn(SourceType sourceType) =>
        sourceType is SourceType.GoogleDrive or SourceType.SharePoint or SourceType.AzureDevOps or SourceType.GitHub;
}
