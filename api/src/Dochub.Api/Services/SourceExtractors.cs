using System.IO.Compression;
using System.Text.Json;
using Dochub.Api.Domain;

namespace Dochub.Api.Services;

/// <summary>One file discovered in a source system, ready to be streamed to blob storage.</summary>
public record ExtractedDocument(
    string Name,
    string RelativePath,
    string SourceLocation,
    string? ContentType,
    string? ExternalId,
    long SizeHint,
    Func<CancellationToken, Task<Stream>> OpenAsync);

/// <summary>Connector-specific options supplied on the upload request.</summary>
public record ExtractionContext(SourceType SourceType, string SourceReference, JsonElement Options, string? AccessToken);

public interface ISourceExtractor
{
    SourceType SourceType { get; }
    /// <summary>Enumerates documents lazily so a large repo/drive never has to fit in memory.</summary>
    IAsyncEnumerable<ExtractedDocument> ExtractAsync(ExtractionContext context, CancellationToken ct);
}

public interface ISourceExtractorFactory
{
    ISourceExtractor Get(SourceType type);
}

public class SourceExtractorFactory(IEnumerable<ISourceExtractor> extractors) : ISourceExtractorFactory
{
    private readonly Dictionary<SourceType, ISourceExtractor> _map = extractors.ToDictionary(x => x.SourceType);

    public ISourceExtractor Get(SourceType type) => _map.TryGetValue(type, out var e)
        ? e
        : throw new NotSupportedException($"No extractor registered for source '{type}'.");
}

/// <summary>
/// Files the client already streamed to the API's staging area (drag-and-drop,
/// "Browse files"/"Browse folder"). A folder arrives as one zip and is expanded here.
/// </summary>
public class LocalFileExtractor(IStagingStore staging, ILogger<LocalFileExtractor> log) : ISourceExtractor
{
    public SourceType SourceType => SourceType.Local;

    public async IAsyncEnumerable<ExtractedDocument> ExtractAsync(
        ExtractionContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!context.Options.TryGetProperty("stagingIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var idElement in ids.EnumerateArray())
        {
            var id = idElement.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;
            var staged = await staging.GetAsync(id, ct);
            if (staged is null) { log.LogWarning("Staged upload {Id} expired before extraction", id); continue; }

            if (staged.IsArchive)
            {
                // A "folder" upload: every entry becomes its own document, path preserved.
                using var archive = ZipFile.OpenRead(staged.PhysicalPath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.Length == 0 && entry.FullName.EndsWith('/')) continue;
                    var path = entry.FullName.Replace('\\', '/');
                    yield return new ExtractedDocument(
                        Name: Path.GetFileName(path),
                        RelativePath: path,
                        SourceLocation: $"{staged.OriginalName}!/{path}",
                        ContentType: MimeTypes.For(path),
                        ExternalId: null,
                        SizeHint: entry.Length,
                        OpenAsync: _ =>
                        {
                            // Reopen per entry: the caller consumes streams one at a time.
                            var a = ZipFile.OpenRead(staged.PhysicalPath);
                            var e = a.GetEntry(entry.FullName)!;
                            return Task.FromResult<Stream>(new DisposingStream(e.Open(), a));
                        });
                }
            }
            else
            {
                yield return new ExtractedDocument(
                    Name: staged.OriginalName,
                    RelativePath: staged.RelativePath ?? staged.OriginalName,
                    SourceLocation: staged.RelativePath ?? staged.OriginalName,
                    ContentType: staged.ContentType ?? MimeTypes.For(staged.OriginalName),
                    ExternalId: null,
                    SizeHint: staged.SizeBytes,
                    OpenAsync: _ => Task.FromResult<Stream>(File.OpenRead(staged.PhysicalPath)));
            }
        }
    }
}

/// <summary>
/// Walks a repository tree through the GitHub REST API. Honours branch, path
/// prefix and an extension allow-list, all supplied in the upload request.
/// </summary>
public class GitHubExtractor(IHttpClientFactory http, ILogger<GitHubExtractor> log) : ISourceExtractor
{
    public SourceType SourceType => SourceType.GitHub;

    public async IAsyncEnumerable<ExtractedDocument> ExtractAsync(
        ExtractionContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var repo = context.Options.GetStringOrDefault("repository")
            ?? throw new InvalidOperationException("GitHub upload requires 'repository' (owner/name).");
        repo = NormalizeRepo(repo);
        var branch = context.Options.GetStringOrDefault("branch") ?? "main";
        var pathPrefix = (context.Options.GetStringOrDefault("path") ?? "").Trim('/');
        var extensions = context.Options.GetStringArray("fileTypes");

        var client = http.CreateClient("github");
        if (!string.IsNullOrWhiteSpace(context.AccessToken))
            client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        var treeUrl = $"repos/{repo}/git/trees/{Uri.EscapeDataString(branch)}?recursive=1";
        using var response = await client.GetAsync(treeUrl, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (doc.RootElement.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean())
            log.LogWarning("GitHub tree for {Repo}@{Branch} was truncated; narrow the path filter", repo, branch);

        foreach (var node in doc.RootElement.GetProperty("tree").EnumerateArray())
        {
            if (node.GetProperty("type").GetString() != "blob") continue;
            var path = node.GetProperty("path").GetString()!;
            if (pathPrefix.Length > 0 && !path.StartsWith(pathPrefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (extensions.Count > 0 && !extensions.Any(x => path.EndsWith("." + x.TrimStart('.'), StringComparison.OrdinalIgnoreCase))) continue;

            var sha = node.GetProperty("sha").GetString()!;
            var size = node.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            var relative = pathPrefix.Length > 0 ? path[(pathPrefix.Length + 1)..] : path;

            yield return new ExtractedDocument(
                Name: Path.GetFileName(path),
                RelativePath: relative,
                SourceLocation: $"{repo}/{path}@{branch}",
                ContentType: MimeTypes.For(path),
                ExternalId: sha,
                SizeHint: size,
                OpenAsync: async token =>
                {
                    var raw = await client.GetAsync($"repos/{repo}/contents/{Uri.EscapeDataString(path)}?ref={Uri.EscapeDataString(branch)}",
                        HttpCompletionOption.ResponseHeadersRead, token);
                    raw.EnsureSuccessStatusCode();
                    return await raw.Content.ReadAsStreamAsync(token);
                });
        }
    }

    private static string NormalizeRepo(string value)
    {
        value = value.Trim();
        if (value.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var idx = value.IndexOf("github.com", StringComparison.OrdinalIgnoreCase);
            value = value[(idx + "github.com".Length)..].Trim('/', ':');
        }
        return value.TrimEnd('/').Replace(".git", "", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>SharePoint document libraries via Microsoft Graph, using the caller's delegated MS token.</summary>
public class SharePointExtractor(IHttpClientFactory http) : ISourceExtractor
{
    public SourceType SourceType => SourceType.SharePoint;

    public async IAsyncEnumerable<ExtractedDocument> ExtractAsync(
        ExtractionContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new InvalidOperationException("SharePoint upload requires a connected Microsoft account.");

        var driveId = context.Options.GetStringOrDefault("driveId")
            ?? throw new InvalidOperationException("SharePoint upload requires 'driveId'.");
        var folderId = context.Options.GetStringOrDefault("itemId") ?? "root";

        var client = http.CreateClient("graph");
        client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        await foreach (var item in GraphItems.EnumerateAsync(client, driveId, folderId, "", ct))
            yield return item;
    }
}

/// <summary>Google Drive folders via the Drive v3 API, using the caller's delegated Google token.</summary>
public class GoogleDriveExtractor(IHttpClientFactory http) : ISourceExtractor
{
    // Google-native docs have no bytes to download; export them to an Office format instead.
    private static readonly Dictionary<string, (string Mime, string Extension)> ExportMap = new()
    {
        ["application/vnd.google-apps.document"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx")
    };

    public SourceType SourceType => SourceType.GoogleDrive;

    public async IAsyncEnumerable<ExtractedDocument> ExtractAsync(
        ExtractionContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(context.AccessToken))
            throw new InvalidOperationException("Google Drive upload requires a connected Google account.");

        var folderId = context.Options.GetStringOrDefault("folderId") ?? "root";
        var client = http.CreateClient("gdrive");
        client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        var queue = new Queue<(string Id, string Path)>();
        queue.Enqueue((folderId, ""));

        while (queue.Count > 0)
        {
            var (id, prefix) = queue.Dequeue();
            string? pageToken = null;
            do
            {
                var url = $"files?q={Uri.EscapeDataString($"'{id}' in parents and trashed=false")}" +
                          "&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime)&pageSize=200&supportsAllDrives=true&includeItemsFromAllDrives=true" +
                          (pageToken is null ? "" : $"&pageToken={pageToken}");
                using var response = await client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

                foreach (var file in doc.RootElement.GetProperty("files").EnumerateArray())
                {
                    var fileId = file.GetProperty("id").GetString()!;
                    var name = file.GetProperty("name").GetString()!;
                    var mime = file.GetProperty("mimeType").GetString()!;

                    if (mime == "application/vnd.google-apps.folder")
                    {
                        queue.Enqueue((fileId, prefix.Length == 0 ? name : $"{prefix}/{name}"));
                        continue;
                    }

                    var export = ExportMap.TryGetValue(mime, out var ex) ? ex : default;
                    var outputName = export.Extension is not null ? name + export.Extension : name;
                    var relative = prefix.Length == 0 ? outputName : $"{prefix}/{outputName}";
                    var size = file.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var parsed) ? parsed : 0;

                    yield return new ExtractedDocument(
                        Name: outputName,
                        RelativePath: relative,
                        SourceLocation: $"drive:/{relative}",
                        ContentType: export.Mime ?? mime,
                        ExternalId: fileId,
                        SizeHint: size,
                        OpenAsync: async token =>
                        {
                            var downloadUrl = export.Mime is not null
                                ? $"files/{fileId}/export?mimeType={Uri.EscapeDataString(export.Mime)}"
                                : $"files/{fileId}?alt=media&supportsAllDrives=true";
                            var raw = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
                            raw.EnsureSuccessStatusCode();
                            return await raw.Content.ReadAsStreamAsync(token);
                        });
                }

                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
            } while (pageToken is not null);
        }
    }
}

/// <summary>Azure DevOps wikis and repos share Graph-free REST shapes; both land as markdown files.</summary>
public class AzureDevOpsExtractor(IHttpClientFactory http) : ISourceExtractor
{
    public SourceType SourceType => SourceType.AzureDevOps;

    public async IAsyncEnumerable<ExtractedDocument> ExtractAsync(
        ExtractionContext context, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var org = context.Options.GetStringOrDefault("organization")
            ?? throw new InvalidOperationException("Azure DevOps upload requires 'organization'.");
        var project = context.Options.GetStringOrDefault("project")
            ?? throw new InvalidOperationException("Azure DevOps upload requires 'project'.");
        var wiki = context.Options.GetStringOrDefault("wiki")
            ?? throw new InvalidOperationException("Azure DevOps upload requires 'wiki'.");

        var client = http.CreateClient("azdo");
        if (!string.IsNullOrWhiteSpace(context.AccessToken))
            client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        var url = $"{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/wiki/wikis/{Uri.EscapeDataString(wiki)}/pages" +
                  "?recursionLevel=full&includeContent=false&api-version=7.1";
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        foreach (var page in Flatten(doc.RootElement))
        {
            var path = page.GetProperty("path").GetString()!;
            if (path == "/") continue;
            var name = path.TrimStart('/').Replace('/', '-') + ".md";

            yield return new ExtractedDocument(
                Name: name,
                RelativePath: path.TrimStart('/') + ".md",
                SourceLocation: $"{org}/{project}/_wiki{path}",
                ContentType: "text/markdown",
                ExternalId: page.TryGetProperty("id", out var id) ? id.ToString() : path,
                SizeHint: 0,
                OpenAsync: async token =>
                {
                    var contentUrl = $"{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(project)}/_apis/wiki/wikis/{Uri.EscapeDataString(wiki)}/pages" +
                                     $"?path={Uri.EscapeDataString(path)}&includeContent=true&api-version=7.1";
                    var raw = await client.GetAsync(contentUrl, token);
                    raw.EnsureSuccessStatusCode();
                    using var pageDoc = JsonDocument.Parse(await raw.Content.ReadAsStringAsync(token));
                    var content = pageDoc.RootElement.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
                    return new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                });
        }
    }

    private static IEnumerable<JsonElement> Flatten(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("subPages", out var subs) || subs.ValueKind != JsonValueKind.Array) yield break;
        foreach (var sub in subs.EnumerateArray())
            foreach (var nested in Flatten(sub)) yield return nested;
    }
}

internal static class GraphItems
{
    public static async IAsyncEnumerable<ExtractedDocument> EnumerateAsync(
        HttpClient client, string driveId, string itemId, string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"drives/{driveId}/items/{itemId}/children?$top=200&$select=id,name,size,file,folder,parentReference";
        while (url is not null)
        {
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

            var children = new List<(string Id, string Name)>();
            foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var name = item.GetProperty("name").GetString()!;

                if (item.TryGetProperty("folder", out _)) { children.Add((id, name)); continue; }
                if (!item.TryGetProperty("file", out var file)) continue;

                var relative = prefix.Length == 0 ? name : $"{prefix}/{name}";
                var size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                var mime = file.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;

                yield return new ExtractedDocument(
                    Name: name, RelativePath: relative, SourceLocation: $"/{relative}",
                    ContentType: mime ?? MimeTypes.For(name), ExternalId: id, SizeHint: size,
                    OpenAsync: async token =>
                    {
                        var raw = await client.GetAsync($"drives/{driveId}/items/{id}/content",
                            HttpCompletionOption.ResponseHeadersRead, token);
                        raw.EnsureSuccessStatusCode();
                        return await raw.Content.ReadAsStreamAsync(token);
                    });
            }

            foreach (var (childId, childName) in children)
            {
                var childPrefix = prefix.Length == 0 ? childName : $"{prefix}/{childName}";
                await foreach (var nested in EnumerateAsync(client, driveId, childId, childPrefix, ct))
                    yield return nested;
            }

            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }
    }
}

/// <summary>Keeps the owning archive alive for as long as the entry stream is read.</summary>
internal sealed class DisposingStream(Stream inner, IDisposable owner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override void Flush() => inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); owner.Dispose(); }
        base.Dispose(disposing);
    }
}

public static class MimeTypes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".doc"] = "application/msword",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".md"] = "text/markdown",
        [".txt"] = "text/plain",
        [".html"] = "text/html",
        [".json"] = "application/json",
        [".yaml"] = "application/yaml",
        [".yml"] = "application/yaml",
        [".csv"] = "text/csv",
        [".adoc"] = "text/asciidoc"
    };

    public static string For(string path) =>
        Map.TryGetValue(Path.GetExtension(path), out var mime) ? mime : "application/octet-stream";
}

public static class JsonElementExtensions
{
    public static string? GetStringOrDefault(this JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public static IReadOnlyList<string> GetStringArray(this JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Cast<string>().ToList(),
            // Accept the UI's comma-separated "md, yaml, adoc" field verbatim.
            JsonValueKind.String => (value.GetString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => []
        };
    }
}
