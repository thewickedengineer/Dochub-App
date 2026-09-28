namespace Dochub.Api.Services;

public record StagedFile(
    string Id, string OriginalName, string? RelativePath, string? ContentType,
    long SizeBytes, string PhysicalPath, bool IsArchive, DateTimeOffset CreatedAt);

/// <summary>
/// Holds bytes the browser streamed in, between "Browse files" and the upload
/// pipeline picking them up. Local disk keeps the request path fast; the files
/// are deleted as soon as they reach blob storage.
/// </summary>
public interface IStagingStore
{
    Task<StagedFile> SaveAsync(string originalName, string? relativePath, string? contentType, Stream content, CancellationToken ct);
    Task<StagedFile?> GetAsync(string id, CancellationToken ct);
    Task DeleteAsync(string id, CancellationToken ct);
}

public class FileSystemStagingStore : IStagingStore
{
    private static readonly string[] ArchiveExtensions = [".zip"];
    private readonly string _root;
    private readonly ILogger<FileSystemStagingStore> _log;

    public FileSystemStagingStore(IConfiguration config, ILogger<FileSystemStagingStore> log)
    {
        _log = log;
        // An unset key and a blank one both mean "use the temp directory".
        var configured = config["Pipeline:StagingPath"];
        _root = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Path.GetTempPath(), "dochub-staging")
            : configured;
        Directory.CreateDirectory(_root);
    }

    public async Task<StagedFile> SaveAsync(string originalName, string? relativePath, string? contentType, Stream content, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("n");
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);

        // Only the basename is trusted on disk; the caller's path lives in metadata.
        var safeName = Path.GetFileName(originalName);
        var physical = Path.Combine(dir, safeName);

        await using (var file = File.Create(physical))
            await content.CopyToAsync(file, ct);

        var info = new FileInfo(physical);
        var staged = new StagedFile(id, safeName, relativePath, contentType, info.Length, physical,
            ArchiveExtensions.Contains(Path.GetExtension(safeName), StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);

        await File.WriteAllTextAsync(Path.Combine(dir, ".meta.json"),
            System.Text.Json.JsonSerializer.Serialize(staged), ct);
        return staged;
    }

    public async Task<StagedFile?> GetAsync(string id, CancellationToken ct)
    {
        var meta = Path.Combine(_root, id, ".meta.json");
        if (!File.Exists(meta)) return null;
        return System.Text.Json.JsonSerializer.Deserialize<StagedFile>(await File.ReadAllTextAsync(meta, ct));
    }

    public Task DeleteAsync(string id, CancellationToken ct)
    {
        var dir = Path.Combine(_root, id);
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Could not clean staging directory {Dir}", dir); }
        return Task.CompletedTask;
    }
}
