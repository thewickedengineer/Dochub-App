using System.Text.Json;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;

namespace Dochub.Api.Services;

/// <summary>What a picked entry needs to be imported: its id, and its drive for Microsoft.</summary>
public record BrowseSelection(string Id, string? DriveId, bool Folder, string Name);

/// <summary>
/// One row in the file browser. <see cref="Location"/> is set when the row can be
/// opened (a place, site, library or folder); <see cref="Selection"/> when it can
/// be picked for import (a folder or a file).
/// </summary>
public record BrowseItem(
    string Name, string Kind, string? Location, BrowseSelection? Selection,
    long? Size, DateTimeOffset? ModifiedAt, string? MimeType, string? Detail);

/// <summary>A page of rows; <see cref="Cursor"/> fetches the next one.</summary>
public record BrowseResult(IReadOnlyList<BrowseItem> Items, string? Cursor, string? Notice);

/// <summary>
/// Lists what a connected account can see, so the UI can offer a SharePoint /
/// OneDrive or Google Drive browser instead of asking for a pasted link.
///
/// Locations are opaque strings the browser hands back unchanged:
/// Microsoft — "" (places), "shared", "sites", "site:{siteId}", "drive:{driveId}:{itemId}";
/// Google — "" (places), "shared", "drives", "folder:{id}".
/// </summary>
public interface ISourceBrowser
{
    Task<BrowseResult> BrowseAsync(SourceType sourceType, string accessToken,
        string? location, string? query, string? cursor, CancellationToken ct);
}

public class SourceBrowser(IHttpClientFactory http) : ISourceBrowser
{
    private const string GraphBase = "https://graph.microsoft.com/v1.0/";
    private const string GraphItemFields = "id,name,size,file,folder,lastModifiedDateTime,parentReference,remoteItem";
    private const string GoogleFields = "nextPageToken,files(id,name,mimeType,size,modifiedTime,owners(displayName))";
    private const string GoogleFolder = "application/vnd.google-apps.folder";

    public Task<BrowseResult> BrowseAsync(SourceType sourceType, string accessToken,
        string? location, string? query, string? cursor, CancellationToken ct) => sourceType switch
    {
        SourceType.SharePoint => BrowseMicrosoftAsync(accessToken, location ?? "", query, cursor, ct),
        SourceType.GoogleDrive => BrowseGoogleAsync(accessToken, location ?? "", query, cursor, ct),
        _ => throw new ArgumentException($"{sourceType.Label()} has no file browser.")
    };

    // ── Microsoft: OneDrive and SharePoint through Graph ─────────────────────────

    private async Task<BrowseResult> BrowseMicrosoftAsync(
        string token, string location, string? query, string? cursor, CancellationToken ct)
    {
        var client = http.CreateClient("graph");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        if (cursor is not null)
        {
            // Graph's next link is a full URL; only ever follow it back to Graph.
            if (!cursor.StartsWith(GraphBase, StringComparison.Ordinal))
                throw new ArgumentException("That page link is not a Microsoft Graph link.");
            return await GraphPageAsync(client, cursor, location, ct);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var drive = location.StartsWith("drive:", StringComparison.Ordinal) ? location.Split(':')[1] : null;
            var search = Uri.EscapeDataString(query.Replace("'", "''"));
            var path = drive is null ? $"me/drive/root/search(q='{search}')" : $"drives/{drive}/root/search(q='{search}')";
            return await GraphPageAsync(client, $"{path}?$top=100&$select={GraphItemFields}", location, ct);
        }

        if (location == "")
        {
            using var me = await GetJsonAsync(client, "me/drive?$select=id,driveType,owner", ct);
            var driveId = me.RootElement.GetProperty("id").GetString()!;
            var business = me.RootElement.TryGetProperty("driveType", out var t) && t.GetString() == "business";
            var places = new List<BrowseItem>
            {
                Place("My files", $"drive:{driveId}:root", "onedrive", "Your OneDrive"),
                Place("Shared with me", "shared", "shared", "Files and folders others shared with you"),
            };
            // A personal Microsoft account has OneDrive but no SharePoint sites.
            if (business) places.Add(Place("SharePoint sites", "sites", "sites", "Sites and their document libraries"));
            return new BrowseResult(places, null, null);
        }

        if (location == "shared")
            return await GraphPageAsync(client, $"me/drive/sharedWithMe?$top=100", location, ct);

        if (location == "sites")
        {
            using var sites = await GetJsonAsync(client, "sites?search=*&$top=100&$select=id,displayName,webUrl,description", ct);
            var items = sites.RootElement.GetProperty("value").EnumerateArray()
                .Select(s => new BrowseItem(
                    s.TryGetProperty("displayName", out var n) ? n.GetString() ?? "Site" : "Site", "site",
                    $"site:{s.GetProperty("id").GetString()}", null, null, null, null,
                    s.TryGetProperty("webUrl", out var w) ? w.GetString() : null))
                .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new BrowseResult(items, null, items.Count == 0 ? "No SharePoint sites are visible to this account." : null);
        }

        if (location.StartsWith("site:", StringComparison.Ordinal))
        {
            var siteId = location["site:".Length..];
            using var drives = await GetJsonAsync(client,
                $"sites/{siteId}/drives?$select=id,name,description,lastModifiedDateTime", ct);
            var items = drives.RootElement.GetProperty("value").EnumerateArray()
                .Select(d =>
                {
                    var id = d.GetProperty("id").GetString()!;
                    var name = d.GetProperty("name").GetString() ?? "Documents";
                    // A whole library can be picked: it is its own root folder.
                    return new BrowseItem(name, "library", $"drive:{id}:root",
                        new BrowseSelection("root", id, true, name), null, ModifiedOf(d, "lastModifiedDateTime"), null, "Document library");
                })
                .ToList();
            return new BrowseResult(items, null, null);
        }

        if (location.StartsWith("drive:", StringComparison.Ordinal))
        {
            var parts = location.Split(':', 3);
            if (parts.Length != 3) throw new ArgumentException("Unknown location.");
            return await GraphPageAsync(client,
                $"drives/{parts[1]}/items/{parts[2]}/children?$top=200&$select={GraphItemFields}&$orderby=name", location, ct);
        }

        throw new ArgumentException("Unknown location.");
    }

    private static async Task<BrowseResult> GraphPageAsync(HttpClient client, string url, string location, CancellationToken ct)
    {
        using var page = await GetJsonAsync(client, url, ct);
        var items = new List<BrowseItem>();
        foreach (var raw in page.RootElement.GetProperty("value").EnumerateArray())
        {
            // Items shared with the user live in someone else's drive: remoteItem says where.
            var item = raw.TryGetProperty("remoteItem", out var remote) ? remote : raw;
            var id = item.GetProperty("id").GetString()!;
            var name = raw.TryGetProperty("name", out var n) ? n.GetString() ?? id : id;
            var driveId = item.TryGetProperty("parentReference", out var parent) && parent.TryGetProperty("driveId", out var d)
                ? d.GetString() : null;
            var folder = item.TryGetProperty("folder", out var f);
            var isFile = item.TryGetProperty("file", out var file);
            if (!folder && !isFile) continue;            // notebooks, packages and the like
            if (driveId is null) continue;

            var count = folder && f.TryGetProperty("childCount", out var c) ? c.GetInt32() : (int?)null;
            items.Add(new BrowseItem(
                name, folder ? "folder" : "file",
                folder ? $"drive:{driveId}:{id}" : null,
                new BrowseSelection(id, driveId, folder, name),
                isFile && raw.TryGetProperty("size", out var size) ? size.GetInt64() : null,
                ModifiedOf(raw, "lastModifiedDateTime"),
                isFile && file.TryGetProperty("mimeType", out var mime) ? mime.GetString() : null,
                count is { } items1 ? $"{items1} item{(items1 == 1 ? "" : "s")}" : null));
        }

        var next = page.RootElement.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
        return new BrowseResult(SortFoldersFirst(items), next,
            items.Count == 0 && next is null ? (location == "shared" ? "Nothing has been shared with this account." : "This folder is empty.") : null);
    }

    // ── Google Drive ──────────────────────────────────────────────────────────────

    private async Task<BrowseResult> BrowseGoogleAsync(
        string token, string location, string? query, string? cursor, CancellationToken ct)
    {
        var client = http.CreateClient("gdrive");
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        if (string.IsNullOrWhiteSpace(query) && location == "")
            return new BrowseResult(
            [
                Place("My Drive", "folder:root", "mydrive", "Your own files"),
                Place("Shared with me", "shared", "shared", "Files and folders others shared with you"),
                Place("Shared drives", "drives", "drives", "Team drives you belong to"),
            ], null, null);

        if (string.IsNullOrWhiteSpace(query) && location == "drives")
        {
            using var drives = await GetJsonAsync(client,
                "drives?pageSize=100" + (cursor is null ? "" : $"&pageToken={Uri.EscapeDataString(cursor)}"), ct);
            var items = drives.RootElement.GetProperty("drives").EnumerateArray()
                .Select(d =>
                {
                    var id = d.GetProperty("id").GetString()!;
                    var name = d.GetProperty("name").GetString() ?? "Shared drive";
                    return new BrowseItem(name, "library", $"folder:{id}", new BrowseSelection(id, null, true, name),
                        null, null, null, "Shared drive");
                })
                .ToList();
            var nextDrives = drives.RootElement.TryGetProperty("nextPageToken", out var nd) ? nd.GetString() : null;
            return new BrowseResult(items, nextDrives, items.Count == 0 ? "This account is not in any shared drive." : null);
        }

        string filter;
        if (!string.IsNullOrWhiteSpace(query))
            filter = $"name contains '{query.Replace("\\", "\\\\").Replace("'", "\\'")}' and trashed=false";
        else if (location == "shared")
            filter = "sharedWithMe and trashed=false";
        else if (location.StartsWith("folder:", StringComparison.Ordinal))
            filter = $"'{location["folder:".Length..].Replace("'", "\\'")}' in parents and trashed=false";
        else
            throw new ArgumentException("Unknown location.");

        var url = $"files?q={Uri.EscapeDataString(filter)}&fields={Uri.EscapeDataString(GoogleFields)}" +
                  "&pageSize=200&orderBy=folder,name&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                  (cursor is null ? "" : $"&pageToken={Uri.EscapeDataString(cursor)}");
        using var page = await GetJsonAsync(client, url, ct);

        var rows = new List<BrowseItem>();
        foreach (var file in page.RootElement.GetProperty("files").EnumerateArray())
        {
            var id = file.GetProperty("id").GetString()!;
            var name = file.GetProperty("name").GetString() ?? id;
            var mime = file.TryGetProperty("mimeType", out var m) ? m.GetString() : null;
            var folder = mime == GoogleFolder;
            // Shortcuts, forms and maps have no content to import.
            if (!folder && mime is "application/vnd.google-apps.shortcut" or "application/vnd.google-apps.form"
                    or "application/vnd.google-apps.map" or "application/vnd.google-apps.site") continue;

            rows.Add(new BrowseItem(name, folder ? "folder" : "file",
                folder ? $"folder:{id}" : null,
                new BrowseSelection(id, null, folder, name),
                file.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var bytes) ? bytes : null,
                ModifiedOf(file, "modifiedTime"), mime,
                location == "shared" && file.TryGetProperty("owners", out var owners) && owners.GetArrayLength() > 0
                    && owners[0].TryGetProperty("displayName", out var owner) ? $"Shared by {owner.GetString()}" : null));
        }

        var next = page.RootElement.TryGetProperty("nextPageToken", out var t) ? t.GetString() : null;
        return new BrowseResult(SortFoldersFirst(rows), next,
            rows.Count == 0 && next is null ? (string.IsNullOrWhiteSpace(query) ? "This folder is empty." : "Nothing matches that search.") : null);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────

    private static BrowseItem Place(string name, string location, string kind, string detail) =>
        new(name, "place:" + kind, location, null, null, null, null, detail);

    private static List<BrowseItem> SortFoldersFirst(List<BrowseItem> items) =>
        items.OrderBy(x => x.Kind == "folder" ? 0 : 1).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static DateTimeOffset? ModifiedOf(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetDateTimeOffset(out var at) ? at : null;

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "The connected account's access has lapsed. Reconnect it.",
                System.Net.HttpStatusCode.Forbidden => "The connected account is not allowed to open this location.",
                System.Net.HttpStatusCode.NotFound => "That location no longer exists, or this account cannot see it.",
                _ => $"The provider answered {(int)response.StatusCode} while listing files."
            });
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
