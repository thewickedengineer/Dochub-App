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
/// Downloads a repository branch as one archive and hands its files on from disk.
/// One API request per import, whatever the size of the repository: anonymous
/// access allows only 60 an hour, so fetching file by file could never finish a
/// repository of any size. Honours path prefix and an extension allow-list.
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
        var hasToken = !string.IsNullOrWhiteSpace(context.AccessToken);
        if (hasToken)
            client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        var workDir = Path.Combine(Path.GetTempPath(), "dochub-github", Guid.NewGuid().ToString("n"));
        try
        {
            // GitHub redirects to codeload.github.com, which serves the archive itself.
            using (var response = await client.GetAsync($"repos/{repo}/tarball/{Uri.EscapeDataString(branch)}",
                       HttpCompletionOption.ResponseHeadersRead, ct))
            {
                EnsureGitHubSuccess(response, repo, branch, hasToken);
                await using var archive = await response.Content.ReadAsStreamAsync(ct);
                await UnpackAsync(archive, workDir, ct);
            }

            var files = ListFiles(workDir);
            log.LogInformation("GitHub {Repo}@{Branch}: {Count} files in the archive", repo, branch, files.Count);

            foreach (var (path, fullPath, size) in files)
            {
                if (pathPrefix.Length > 0 && !path.StartsWith(pathPrefix + "/", StringComparison.OrdinalIgnoreCase)) continue;
                if (extensions.Count > 0 && !extensions.Any(x => path.EndsWith("." + x.TrimStart('.'), StringComparison.OrdinalIgnoreCase))) continue;

                var relative = pathPrefix.Length > 0 ? path[(pathPrefix.Length + 1)..] : path;
                yield return new ExtractedDocument(
                    Name: Path.GetFileName(path),
                    RelativePath: relative,
                    SourceLocation: $"{repo}/{path}@{branch}",
                    ContentType: MimeTypes.For(path),
                    // The path is the file's identity in the repository; two files
                    // can share content (empty files, copies).
                    ExternalId: $"{repo}@{branch}:{path}",
                    SizeHint: size,
                    OpenAsync: _ => Task.FromResult<Stream>(File.OpenRead(fullPath)));
            }
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true); }
            catch (IOException ex) { log.LogWarning(ex, "Could not remove {Dir}", workDir); }
        }
    }

    /// <summary>Unpacks a gzipped tar archive into <paramref name="workDir"/>.</summary>
    public static async Task UnpackAsync(Stream gzippedTar, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        await using var gzip = new System.IO.Compression.GZipStream(gzippedTar, System.IO.Compression.CompressionMode.Decompress);
        // Rejects entries that would land outside workDir.
        await System.Formats.Tar.TarFile.ExtractToDirectoryAsync(gzip, workDir, overwriteFiles: true, ct);
    }

    /// <summary>
    /// The regular files of an unpacked archive, by their path in the repository.
    /// GitHub wraps everything in one "owner-repo-sha" folder, which is not part of the path.
    /// </summary>
    public static List<(string Path, string FullPath, long Size)> ListFiles(string workDir)
    {
        var dirs = Directory.GetDirectories(workDir);
        var root = dirs.Length == 1 && Directory.GetFiles(workDir).Length == 0 ? dirs[0] : workDir;
        return new DirectoryInfo(root)
            .EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .Select(f => (Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.FullName, f.Length))
            .OrderBy(f => f.Item1, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// GitHub answers 403 for a spent rate limit as well as for a refused token,
    /// and 404 for a private repository read without access. Say which it was,
    /// rather than telling someone with no token that their token was rejected.
    /// </summary>
    private static void EnsureGitHubSuccess(HttpResponseMessage response, string repo, string branch, bool hasToken)
    {
        if (response.IsSuccessStatusCode) return;
        var status = (int)response.StatusCode;
        var remaining = response.Headers.TryGetValues("x-ratelimit-remaining", out var r) ? r.FirstOrDefault() : null;
        if (status == 429 || (status == 403 && remaining == "0"))
        {
            var reset = response.Headers.TryGetValues("x-ratelimit-reset", out var s) && long.TryParse(s.FirstOrDefault(), out var epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch) : (DateTimeOffset?)null;
            throw new GitHubRateLimitException(
                $"GitHub's rate limit is used up{(reset is { } at ? $" until {at:HH:mm} UTC" : "")}. " +
                (hasToken ? "Try again after that." : "Anonymous access allows 60 requests an hour; connect GitHub with a token for 5,000."));
        }
        if (status == 404)
            throw new InvalidOperationException(hasToken
                ? $"GitHub can't find {repo}@{branch}, or the connected token can't read it."
                : $"GitHub can't find {repo}@{branch}. If it's private, connect GitHub with a token that can read it.");
        response.EnsureSuccessStatusCode();
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

        var client = http.CreateClient("graph");
        client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        // Picked in the browser: any mix of folders and files, possibly across drives.
        var picked = PickedItem.Read(context.Options);
        if (picked.Count > 0)
        {
            foreach (var item in picked)
            {
                if (item.DriveId is null)
                    throw new InvalidOperationException($"'{item.Name}' was picked without its drive. Pick it again.");
                if (!item.Folder)
                {
                    yield return await GraphItems.FileAsync(client, item.DriveId, item.Id, ct);
                    continue;
                }
                // With several picks, each folder keeps its own name so files cannot collide.
                var prefix = picked.Count == 1 ? "" : item.Name;
                await foreach (var file in GraphItems.EnumerateAsync(client, item.DriveId, item.Id, prefix, ct))
                    yield return file;
            }
            yield break;
        }

        var driveId = context.Options.GetStringOrDefault("driveId")
            ?? throw new InvalidOperationException("SharePoint upload requires 'driveId'.");
        var folderId = context.Options.GetStringOrDefault("itemId") ?? "root";

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

        var client = http.CreateClient("gdrive");
        client.DefaultRequestHeaders.Authorization = new("Bearer", context.AccessToken);

        var queue = new Queue<(string Id, string Path)>();

        // Picked in the browser: files are fetched one by one, folders walked like any other.
        var picked = PickedItem.Read(context.Options);
        foreach (var item in picked)
        {
            if (item.Folder) { queue.Enqueue((item.Id, picked.Count == 1 ? "" : item.Name)); continue; }

            using var meta = await client.GetAsync(
                $"files/{item.Id}?fields=id,name,mimeType,size,modifiedTime&supportsAllDrives=true", ct);
            meta.EnsureSuccessStatusCode();
            using var one = JsonDocument.Parse(await meta.Content.ReadAsStringAsync(ct));
            yield return DriveDocument(client, one.RootElement, "");
        }
        if (picked.Count == 0)
            queue.Enqueue((context.Options.GetStringOrDefault("folderId") ?? "root", ""));

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

                    yield return DriveDocument(client, file, prefix);
                }

                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var nt) ? nt.GetString() : null;
            } while (pageToken is not null);
        }
    }

    private static ExtractedDocument DriveDocument(HttpClient client, JsonElement file, string prefix)
    {
        var fileId = file.GetProperty("id").GetString()!;
        var name = file.GetProperty("name").GetString()!;
        var mime = file.GetProperty("mimeType").GetString()!;
        var export = ExportMap.TryGetValue(mime, out var ex) ? ex : default;
        var outputName = export.Extension is not null ? name + export.Extension : name;
        var relative = prefix.Length == 0 ? outputName : $"{prefix}/{outputName}";
        var size = file.TryGetProperty("size", out var sz) && long.TryParse(sz.GetString(), out var parsed) ? parsed : 0;

        return new ExtractedDocument(
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

/// <summary>A folder or file chosen in the source browser, as stored in the upload's options.</summary>
public record PickedItem(string Id, string? DriveId, bool Folder, string Name)
{
    public static IReadOnlyList<PickedItem> Read(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];
        return items.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("id", out _))
            .Select(x => new PickedItem(
                x.GetProperty("id").GetString()!,
                x.GetStringOrDefault("driveId"),
                x.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.True,
                x.GetStringOrDefault("name") ?? x.GetProperty("id").GetString()!))
            .ToList();
    }
}

internal static class GraphItems
{
    /// <summary>A single picked file, fetched by id.</summary>
    public static async Task<ExtractedDocument> FileAsync(HttpClient client, string driveId, string id, CancellationToken ct)
    {
        using var response = await client.GetAsync($"drives/{driveId}/items/{id}?$select=id,name,size,file", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var item = doc.RootElement;
        var name = item.GetProperty("name").GetString()!;
        var mime = item.TryGetProperty("file", out var file) && file.TryGetProperty("mimeType", out var mt) ? mt.GetString() : null;
        return Document(client, driveId, id, name, name, item.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0, mime);
    }

    private static ExtractedDocument Document(HttpClient client, string driveId, string id, string name,
        string relative, long size, string? mime) =>
        new(Name: name, RelativePath: relative, SourceLocation: $"/{relative}",
            ContentType: mime ?? MimeTypes.For(name), ExternalId: id, SizeHint: size,
            OpenAsync: async token =>
            {
                var raw = await client.GetAsync($"drives/{driveId}/items/{id}/content",
                    HttpCompletionOption.ResponseHeadersRead, token);
                raw.EnsureSuccessStatusCode();
                return await raw.Content.ReadAsStreamAsync(token);
            });

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

                yield return Document(client, driveId, id, name, relative, size, mime);
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

/// <summary>A provider's rate limit is spent: transient, and not the token's fault.</summary>
public sealed class GitHubRateLimitException(string message) : InvalidOperationException(message);
