using Dochub.Api.Services;

namespace Dochub.Api.Tests;

/// <summary>
/// A personal OneDrive folder copied from the address bar is not a sharing link,
/// so Graph's /shares lookup refuses it; the drive and item are read from the URL.
/// </summary>
public class OneDriveLinkTests
{
    [Theory]
    [InlineData("https://onedrive.live.com/?cid=ABC123&id=ABC123%21456", "drives/ABC123/items/ABC123%21456")]
    [InlineData("https://onedrive.live.com/redir?resid=ABC123%21789", "drives/ABC123/items/ABC123%21789")]
    [InlineData("https://onedrive.live.com/?id=%2Fpersonal%2Fabc123%2FDocuments%2FClaims%2F2026%20Q1", "drives/abc123/root:/Claims/2026%20Q1:")]
    [InlineData("https://onedrive.live.com/?id=%2Fpersonal%2Fabc123%2FDocuments", "drives/abc123/root")]
    public void Personal_OneDrive_addresses_name_their_drive_and_item(string url, string expected) =>
        Assert.Equal(expected, ShareLinkResolver.OneDriveItemPath(new Uri(url)));

    [Theory]
    [InlineData("https://onedrive.live.com/")]
    [InlineData("https://contoso-my.sharepoint.com/personal/ann/Documents/Claims")]
    public void Other_links_are_left_to_the_sharing_lookup(string url) =>
        Assert.Null(ShareLinkResolver.OneDriveItemPath(new Uri(url)));
}
