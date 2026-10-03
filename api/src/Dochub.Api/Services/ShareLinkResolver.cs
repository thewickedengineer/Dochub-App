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
            || host is "1drv.ms" or "onedrive.live.com" or "onedrive.com"
            || host.EndsWith(".onedrive.com", StringComparison.Ordinal))
            return await ResolveMicrosoftAsync(uri, accessToken, ct);

        throw new ArgumentException(
            $"'{uri.Host}' is not a Google Drive, SharePoint or OneDrive link. Paste a link from one of those.", nameof(link));
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

    /// <summary>
    /// The Graph path for a personal OneDrive address-bar URL, or null when the URL
    /// carries no drive and item. Two shapes exist: the classic
    /// <c>?cid=…&amp;id=CID!123</c> (or <c>resid=CID!123</c>) and the newer
    /// <c>?id=/personal/{cid}/Documents/Folder</c>.
    /// </summary>
    public static string? OneDriveItemPath(Uri uri)
    {
        if (!uri.Host.EndsWith("onedrive.live.com", StringComparison.OrdinalIgnoreCase)) return null;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        string? Get(string key) => query.TryGetValue(key, out var v) ? v.ToString() : null;

        var id = Get("id") ?? Get("resid");
        if (string.IsNullOrWhiteSpace(id)) return null;

        if (id.StartsWith("/personal/", StringComparison.OrdinalIgnoreCase))
        {
            var parts = id.Trim('/').Split('/', 4);      // personal, cid, Documents, rest
            if (parts.Length < 3) return null;
            var drive = parts[1];
            var path = parts.Length == 4 ? parts[3] : "";
            return path.Length == 0
                ? $"drives/{drive}/root"
                : $"drives/{drive}/root:/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}:";
        }

        var cid = Get("cid") ?? (id.Contains('!') ? id[..id.IndexOf('!')] : null);
        return string.IsNullOrWhiteSpace(cid) ? null : $"drives/{cid}/items/{Uri.EscapeDataString(id)}";
    }

    private async Task<ResolvedLink> ResolveMicrosoftAsync(Uri uri, string accessToken, CancellationToken ct)
    {
        // Graph resolves any sharing URL — including the long ?id=/sourcedoc= ones
        // SharePoint produces — through /shares, so there is nothing to parse.
        var encoded = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(uri.ToString()))
            .TrimEnd('=').Replace('/', '_').Replace('+', '-');

        var client = http.CreateClient("graph");
        client.DefaultRequestHeaders.Authorization = new("Bearer", accessToken);

        const string select = "?$select=id,name,folder,file,parentReference,webUrl";
        var response = await client.GetAsync($"shares/{encoded}/driveItem{select}", ct);

        // A personal OneDrive folder's address bar is not a sharing link, but it
        // names the drive and the item, which Graph can read directly.
        if (!response.IsSuccessStatusCode && OneDriveItemPath(uri) is { } direct)
        {
            response.Dispose();
            response = await client.GetAsync(direct + select, ct);
        }

        using var lookup = response;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.NotFound =>
                    "That SharePoint or OneDrive location does not exist, or the signed-in account cannot see it.",
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

        var where = parent.TryGetProperty("driveType", out var driveType)
                    && driveType.GetString() is "personal" or "business"
            ? "OneDrive" : "SharePoint";
        log.LogInformation("Resolved {Where} link to {Name} ({ItemId})", where, name, itemId);

        return new ResolvedLink(
            SourceType.SharePoint,
            name,
            $"{where} › {name}",
            new Dictionary<string, object?>
            {
                ["driveId"] = driveId.GetString(),
                ["itemId"] = itemId
            });
    }
}
