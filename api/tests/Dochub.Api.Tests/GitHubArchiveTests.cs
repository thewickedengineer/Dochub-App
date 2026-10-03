using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Dochub.Api.Services;

namespace Dochub.Api.Tests;

/// <summary>
/// A GitHub import downloads the branch as one archive; files are read from it
/// by their path in the repository, without GitHub's wrapping folder.
/// </summary>
public class GitHubArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dochub-archive-test", Guid.NewGuid().ToString("n"));

    private static MemoryStream Archive(params (string Path, string Content)[] files)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            // GitHub archives open with a global header carrying the commit id.
            tar.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "abc123" }));
            tar.WriteEntry(new PaxTarEntry(TarEntryType.Directory, "acme-handbook-abc123/"));
            foreach (var (path, content) in files)
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, $"acme-handbook-abc123/{path}")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes(content))
                });
        }
        buffer.Position = 0;
        return buffer;
    }

    [Fact]
    public async Task Files_are_listed_by_their_repository_path()
    {
        await GitHubExtractor.UnpackAsync(Archive(("README.md", "hello"), ("src/app.cs", "class A {}"), ("src/empty.txt", "")), _dir, default);

        var files = GitHubExtractor.ListFiles(_dir);

        Assert.Equal(["README.md", "src/app.cs", "src/empty.txt"], files.Select(f => f.Path));
        Assert.Equal("class A {}", await File.ReadAllTextAsync(files[1].FullPath));
        Assert.Equal(0, files[2].Size);
    }

    [Fact]
    public async Task Identical_files_stay_separate()
    {
        await GitHubExtractor.UnpackAsync(Archive(("a/Error.cshtml", "same"), ("b/Error.cshtml", "same")), _dir, default);

        Assert.Equal(2, GitHubExtractor.ListFiles(_dir).Count);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }
}
