using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dochub.Api.Domain;

namespace Dochub.Api.Services;

public record ResolvedLink(
    SourceType SourceType,
    string DisplayName,
    string SourceReference,
    Dictionary<string, object?> Options);

/// <summary>
/// Turns a link someone copied out of Drive or SharePoint into the ids the
/// connectors need. People have a URL in their clipboard, never a driveId, so
/// asking for raw ids pushes them into developer tooling to answer a question
/// the API can answer itself.
/// </summary>
public interface IShareLinkResolver
{
    Task<ResolvedLink> ResolveAsync(string link, string accessToken, CancellationToken ct);
}

public partial class ShareLinkResolver(IHttpClientFactory http, ILogger<ShareLinkResolver> log) : IShareLinkResolver
{
    [GeneratedRegex(@"/(?:folders|file/d|drive/u/\d+/folders)/([A-Za-z0-9_\-]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleIdInPath();

    [GeneratedRegex(@"[?&]id=([A-Za-z0-9_\-]{10,})", RegexOptions.IgnoreCase)]
    private static partial Regex GoogleIdInQuery();

    public async Task<ResolvedLink> ResolveAsync(string link, string accessToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(link))
            throw new ArgumentException("Paste the link to the folder you want to import.", nameof(link));

        link = link.Trim();
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("That does not look like a link. Copy the address from your browser.", nameof(link));

        var host = uri.Host.ToLowerInvariant();

        if (host is "drive.google.com" or "docs.google.com")
            return await ResolveGoogleAsync(uri, accessToken, ct);

        if (host.EndsWith("sharepoint.com", StringComparison.Ordinal)
            || host.EndsWith("sharepoint.us", StringComparison.Ordinal)
            || host is "1drv.ms"
            || host.EndsWith("-my.sharepoint.com", StringComparison.Ordinal))
            return await ResolveMicrosoftAsync(uri, accessToken, ct);

        throw new ArgumentException(
            $"'{uri.Host}' is not a Google Drive or SharePoint link. Paste a link from one of those.", nameof(link));
    }

    private async Task<ResolvedLink> ResolveGoogleAsync(Uri uri, string accessToken, CancellationToken ct)
    {
        var id = GoogleIdInPath().Match(uri.AbsolutePath) is { Success: true } path
            ? path.Groups[1].Value
            : GoogleIdInQuery().Match(uri.Query) is { Success: true } query
                ? query.Groups[1].Value
                : null;

        if (id is null)
            throw new ArgumentException(
                "That Drive link has no folder id in it. Open the folder in Drive and copy the address bar.", nameof(uri));

        // Confirm it exists, that the signed-in account can see it, and that it is
        // a folder — all three fail confusingly later otherwise.
        var client = http.CreateClient("gdrive");
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        using var response = await client.GetAsync(
            $"files/{id}?fields=id,name,mimeType&supportsAllDrives=true", ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound =>
                    "That Drive folder does not exist, or the signed-in account cannot see it.",
                System.Net.HttpStatusCode.Forbidden =>
                    "The signed-in Google account does not have access to that folder.",
                _ => $"Google Drive rejected the lookup ({(int)response.StatusCode})."
            });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        var name = root.GetProperty("name").GetString() ?? "Drive folder";
        var mimeType = root.GetProperty("mimeType").GetString();

        if (mimeType != "application/vnd.google-apps.folder")
            throw new InvalidOperationException(
                $"'{name}' is a file, not a folder. Link the folder that contains the documents.");

        log.LogInformation("Resolved Drive link to folder {Name} ({Id})", name, id);

        return new ResolvedLink(
            SourceType.GoogleDrive,
            name,
            $"Google Drive › {name}",
            new Dictionary<string, object?> { ["folderId"] = id });
    }

    private async Task<ResolvedLink> ResolveMicrosoftAsync(Uri uri, string accessToken, CancellationToken ct)
    {
        // Graph resolves any sharing URL — including the long ?id=/sourcedoc= ones
        // SharePoint produces — through /shares, so there is nothing to parse.
        var encoded = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.ToString()))
            .TrimEnd('=').Replace('/', '_').Replace('+', '-');

        var client = http.CreateClient("graph");
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        using var response = await client.GetAsync(
            $"shares/{encoded}/driveItem?$select=id,name,folder,file,parentReference,webUrl", ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound =>
                    "That SharePoint location does not exist, or the signed-in account cannot see it.",
                System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized =>
                    "The signed-in Microsoft account does not have access to that location.",
                _ => $"Microsoft Graph rejected the lookup ({(int)response.StatusCode})."
            });

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "SharePoint folder" : "SharePoint folder";
        var itemId = root.GetProperty("id").GetString()!;

        if (!root.TryGetProperty("folder", out _))
            throw new InvalidOperationException(
                $"'{name}' is a file, not a folder. Link the folder or document library that contains the documents.");

        if (!root.TryGetProperty("parentReference", out var parent)
            || !parent.TryGetProperty("driveId", out var driveId))
            throw new InvalidOperationException(
                "Microsoft Graph did not say which drive that folder belongs to. Try the folder's own link.");

        log.LogInformation("Resolved SharePoint link to {Name} ({ItemId})", name, itemId);

        return new ResolvedLink(
            SourceType.SharePoint,
            name,
            $"SharePoint › {name}",
            new Dictionary<string, object?>
            {
                ["driveId"] = driveId.GetString(),
                ["itemId"] = itemId
            });
    }
}
