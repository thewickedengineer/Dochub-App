using System.Web;
using Dochub.Api.Domain;
using Dochub.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Tests;

/// <summary>
/// The authorize request and its state are what stand between a user's Drive and
/// anyone who can get a callback delivered, so both are covered directly.
/// </summary>
public class OAuthFlowTests
{
    private static OAuthFlowService Build(out OAuthOptions options)
    {
        options = new OAuthOptions
        {
            RedirectUri = "http://localhost:5173/oauth/callback",
            Google = new OAuthProviderOptions
            {
                ClientId = "google-client-id",
                AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                TokenEndpoint = "https://oauth2.googleapis.com/token",
                Scopes = "openid email https://www.googleapis.com/auth/drive.readonly"
            },
            Microsoft = new OAuthProviderOptions
            {
                ClientId = "ms-client-id",
                Tenant = "common",
                Scopes = "openid email offline_access Files.Read.All Sites.Read.All"
            }
        };

        return new OAuthFlowService(
            Options.Create(options),
            DataProtectionProvider.Create(nameof(OAuthFlowTests)),
            new StubHttpClientFactory(),
            NullLogger<OAuthFlowService>.Instance);
    }

    private static Dictionary<string, string> QueryOf(string url)
    {
        var parsed = HttpUtility.ParseQueryString(new Uri(url).Query);
        return parsed.AllKeys.Where(k => k is not null)
            .ToDictionary(k => k!, k => parsed[k] ?? "");
    }

    [Fact]
    public void Google_authorize_url_asks_for_offline_access()
    {
        var service = Build(out _);
        var request = service.BuildAuthorizeRequest(
            SourceProvider.Google, Guid.NewGuid(), Guid.NewGuid(), SourceType.GoogleDrive);
        var query = QueryOf(request.Url);

        // Without both of these Google returns no refresh token, and any schedule
        // silently stops working an hour later.
        Assert.Equal("offline", query["access_type"]);
        Assert.Equal("consent", query["prompt"]);
        Assert.Contains("drive.readonly", query["scope"]);
    }

    [Fact]
    public void Microsoft_authorize_url_asks_for_offline_access()
    {
        var service = Build(out _);
        var request = service.BuildAuthorizeRequest(
            SourceProvider.Microsoft, Guid.NewGuid(), Guid.NewGuid(), SourceType.SharePoint);

        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize", request.Url);
        Assert.Contains("offline_access", QueryOf(request.Url)["scope"]);
    }

    [Fact]
    public void Authorize_url_uses_pkce_and_never_carries_a_secret()
    {
        var service = Build(out var options);
        options.Google.ClientSecret = "super-secret";

        var request = service.BuildAuthorizeRequest(
            SourceProvider.Google, Guid.NewGuid(), Guid.NewGuid(), SourceType.GoogleDrive);
        var query = QueryOf(request.Url);

        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        Assert.DoesNotContain("super-secret", request.Url);
    }

    [Fact]
    public void The_state_is_opaque_and_does_not_leak_the_pkce_verifier()
    {
        var service = Build(out _);
        var organizationId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        var request = service.BuildAuthorizeRequest(
            SourceProvider.Google, organizationId, userId, SourceType.GoogleDrive);

        // The state travels through the provider, so it must reveal nothing.
        Assert.DoesNotContain(organizationId.ToString(), request.State);
        Assert.DoesNotContain(userId.ToString(), request.State);
        Assert.DoesNotContain("codeVerifier", request.State, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(QueryOf(request.Url)["code_challenge"], request.State);
    }

    [Fact]
    public async Task A_tampered_state_is_refused()
    {
        var service = Build(out _);
        var request = service.BuildAuthorizeRequest(
            SourceProvider.Google, Guid.NewGuid(), Guid.NewGuid(), SourceType.GoogleDrive);

        var tampered = request.State[..^4] + "AAAA";
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CompleteAsync("any-code", tampered, default));

        Assert.Contains("not valid", error.Message);
    }

    [Fact]
    public async Task A_state_minted_with_another_key_is_refused()
    {
        var mine = Build(out _);
        var theirs = new OAuthFlowService(
            Options.Create(new OAuthOptions { Google = new OAuthProviderOptions { ClientId = "x" } }),
            DataProtectionProvider.Create("a-different-application"),
            new StubHttpClientFactory(),
            NullLogger<OAuthFlowService>.Instance);

        var forged = theirs.BuildAuthorizeRequest(
            SourceProvider.Google, Guid.NewGuid(), Guid.NewGuid(), SourceType.GoogleDrive);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => mine.CompleteAsync("any-code", forged.State, default));
    }

    [Fact]
    public void An_unconfigured_provider_fails_with_a_message_naming_the_setting()
    {
        var service = new OAuthFlowService(
            Options.Create(new OAuthOptions()),   // no client ids
            DataProtectionProvider.Create(nameof(OAuthFlowTests)),
            new StubHttpClientFactory(),
            NullLogger<OAuthFlowService>.Instance);

        Assert.False(service.IsConfigured(SourceProvider.Google));
        var error = Assert.Throws<InvalidOperationException>(() => service.BuildAuthorizeRequest(
            SourceProvider.Google, Guid.NewGuid(), Guid.NewGuid(), SourceType.GoogleDrive));
        Assert.Contains("OAuth:Google:ClientId", error.Message);
    }

    [Fact]
    public void The_authorize_url_always_carries_a_redirect_uri()
    {
        var service = Build(out var options);
        var request = service.BuildAuthorizeRequest(
            SourceProvider.Microsoft, Guid.NewGuid(), Guid.NewGuid(), SourceType.SharePoint);

        // Query building drops null values, so a missing reply address would leave
        // the parameter off entirely and the provider would answer AADSTS900971.
        Assert.Equal(options.RedirectUri, QueryOf(request.Url)["redirect_uri"]);
        Assert.Equal(options.RedirectUri, request.RedirectUri);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_redirect_uri_is_refused_with_the_setting_named(string? redirectUri)
    {
        var service = Build(out var options);
        options.RedirectUri = redirectUri!;

        var error = Assert.Throws<InvalidOperationException>(() => service.BuildAuthorizeRequest(
            SourceProvider.Microsoft, Guid.NewGuid(), Guid.NewGuid(), SourceType.SharePoint));

        Assert.Contains("OAuth:RedirectUri", error.Message);
    }

    [Theory]
    [InlineData("localhost:5173/oauth/callback")]          // no scheme
    [InlineData("/oauth/callback")]                        // relative
    [InlineData("ftp://localhost/oauth/callback")]         // wrong scheme
    [InlineData("http://localhost:5173/cb#fragment")]      // providers reject fragments
    public void A_malformed_redirect_uri_is_refused(string redirectUri)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => OAuthFlowService.RequireRedirectUri(redirectUri));
        Assert.Contains("OAuth:RedirectUri", error.Message);
    }

    [Theory]
    [InlineData("http://localhost:5173/oauth/callback")]
    [InlineData("https://dochub.example.com/oauth/callback")]
    public void A_valid_redirect_uri_is_accepted(string redirectUri) =>
        OAuthFlowService.RequireRedirectUri(redirectUri);

    [Theory]
    [InlineData(SourceType.GoogleDrive, SourceProvider.Google)]
    [InlineData(SourceType.SharePoint, SourceProvider.Microsoft)]
    [InlineData(SourceType.AzureDevOps, SourceProvider.Microsoft)]
    public void Sources_map_to_the_provider_that_owns_them(SourceType source, SourceProvider expected) =>
        Assert.Equal(expected, SourceTokenProvider.ProviderFor(source));

    [Theory]
    [InlineData(SourceType.Local)]
    [InlineData(SourceType.Confluence)]
    public void Sources_without_an_external_provider_say_so(SourceType source)
    {
        Assert.False(SourceTokenProvider.SupportsExternalSignIn(source));
        Assert.Throws<NotSupportedException>(() => SourceTokenProvider.ProviderFor(source));
    }
}

internal class StubHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
