using System.Net;
using System.Text;
using System.Text.Json;
using Dochub.Api.Domain;
using Dochub.Api.Services;

namespace Dochub.Api.Tests;

/// <summary>
/// The SharePoint / OneDrive and Google Drive browser, and importing what was picked
/// in it — folders and single files together — against canned provider answers.
/// </summary>
public class SourceBrowserTests
{
    /// <summary>Answers by URL substring; records what was asked.</summary>
    private sealed class Canned(params (string Match, string Body)[] answers) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Asked.Add(url);
            var hit = answers.FirstOrDefault(a => url.Contains(a.Match, StringComparison.Ordinal));
            return Task.FromResult(hit.Body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(hit.Body, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false)
        {
            BaseAddress = new Uri(name == "gdrive" ? "https://www.googleapis.com/drive/v3/" : "https://graph.microsoft.com/v1.0/")
        };
    }

    [Fact]
    public async Task A_work_account_sees_its_OneDrive_shared_items_and_SharePoint_sites()
    {
        var browser = new SourceBrowser(new Factory(new Canned(("me/drive?", """{"id":"d1","driveType":"business"}"""))));

        var top = await browser.BrowseAsync(SourceType.SharePoint, "t", "", null, null, default);

        Assert.Equal(["My files", "Shared with me", "SharePoint sites"], top.Items.Select(x => x.Name));
        Assert.Equal("drive:d1:root", top.Items[0].Location);
    }

    [Fact]
    public async Task A_personal_account_has_no_SharePoint_sites()
    {
        var browser = new SourceBrowser(new Factory(new Canned(("me/drive?", """{"id":"d1","driveType":"personal"}"""))));

        var top = await browser.BrowseAsync(SourceType.SharePoint, "t", "", null, null, default);

        Assert.DoesNotContain(top.Items, x => x.Name == "SharePoint sites");
    }

    [Fact]
    public async Task A_folder_lists_folders_first_and_everything_can_be_picked()
    {
        var browser = new SourceBrowser(new Factory(new Canned(("drives/d1/items/root/children", """
            {"value":[
              {"id":"f1","name":"zeta.pdf","size":10,"file":{"mimeType":"application/pdf"},"parentReference":{"driveId":"d1"}},
              {"id":"f2","name":"Claims","folder":{"childCount":3},"parentReference":{"driveId":"d1"}},
              {"id":"f3","name":"Notebook","package":{"type":"oneNote"},"parentReference":{"driveId":"d1"}}
            ],"@odata.nextLink":"https://graph.microsoft.com/v1.0/drives/d1/items/root/children?$skiptoken=x"}
            """))));

        var page = await browser.BrowseAsync(SourceType.SharePoint, "t", "drive:d1:root", null, null, default);

        Assert.Equal(["Claims", "zeta.pdf"], page.Items.Select(x => x.Name));
        Assert.Equal("drive:d1:f2", page.Items[0].Location);
        Assert.Equal(new BrowseSelection("f1", "d1", false, "zeta.pdf"), page.Items[1].Selection);
        Assert.Equal("3 items", page.Items[0].Detail);
        Assert.StartsWith("https://graph.microsoft.com/", page.Cursor);
    }

    [Fact]
    public async Task A_page_cursor_only_ever_leads_back_to_Graph()
    {
        var browser = new SourceBrowser(new Factory(new Canned()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            browser.BrowseAsync(SourceType.SharePoint, "t", "drive:d1:root", null, "https://evil.example/steal", default));
    }

    [Fact]
    public async Task Items_shared_with_me_open_in_their_owners_drive()
    {
        var browser = new SourceBrowser(new Factory(new Canned(("me/drive/sharedWithMe", """
            {"value":[{"id":"local","name":"Board pack","remoteItem":{"id":"r1","folder":{"childCount":1},"parentReference":{"driveId":"other"}}}]}
            """))));

        var page = await browser.BrowseAsync(SourceType.SharePoint, "t", "shared", null, null, default);

        Assert.Equal("drive:other:r1", page.Items.Single().Location);
        Assert.Equal("other", page.Items.Single().Selection!.DriveId);
    }

    [Fact]
    public async Task Google_lists_a_folder_and_skips_shortcuts()
    {
        var handler = new Canned(("files?q=", """
            {"files":[
              {"id":"g1","name":"Policies","mimeType":"application/vnd.google-apps.folder"},
              {"id":"g2","name":"Rates","mimeType":"application/vnd.google-apps.spreadsheet","modifiedTime":"2026-09-01T10:00:00Z"},
              {"id":"g3","name":"link","mimeType":"application/vnd.google-apps.shortcut"}
            ]}
            """));
        var browser = new SourceBrowser(new Factory(handler));

        var page = await browser.BrowseAsync(SourceType.GoogleDrive, "t", "folder:root", null, null, default);

        Assert.Equal(["Policies", "Rates"], page.Items.Select(x => x.Name));
        Assert.Contains("'root' in parents", Uri.UnescapeDataString(handler.Asked.Single()));
    }

    [Fact]
    public async Task Picked_folders_and_files_are_imported_together()
    {
        var handler = new Canned(
            ("drives/d1/items/fileA?", """{"id":"fileA","name":"memo.docx","size":5,"file":{"mimeType":"application/msword"}}"""),
            ("drives/d2/items/folderB/children", """{"value":[{"id":"x1","name":"a.txt","size":1,"file":{}}]}"""));
        var extractor = new SharePointExtractor(new Factory(handler));
        var options = JsonDocument.Parse("""
            {"items":[
              {"id":"fileA","driveId":"d1","folder":false,"name":"memo.docx"},
              {"id":"folderB","driveId":"d2","folder":true,"name":"Claims"}
            ]}
            """).RootElement;

        var files = new List<ExtractedDocument>();
        await foreach (var file in extractor.ExtractAsync(new ExtractionContext(SourceType.SharePoint, "x", options, "t"), default))
            files.Add(file);

        Assert.Equal(["memo.docx", "Claims/a.txt"], files.Select(f => f.RelativePath));
    }
}
