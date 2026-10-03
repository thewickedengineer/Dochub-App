using System.Net;
using System.Text;
using System.Web;
using Dochub.Api.Domain;
using Dochub.Api.Endpoints;
using Dochub.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Dochub.Api.Tests;

/// <summary>
/// A private repository is connected by signing in to GitHub in a popup, the same
/// way Drive and SharePoint are. GitHub's token endpoint differs in small ways.
/// </summary>
public class GitHubSignInTests
{
    private sealed class Answer(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static OAuthFlowService Build(HttpMessageHandler handler) => new(
        Options.Create(new OAuthOptions
        {
            RedirectUri = "http://localhost:5173/oauth/callback",
            GitHub = new OAuthProviderOptions
            {
                ClientId = "gh-client", ClientSecret = "gh-secret",
                AuthorizeEndpoint = "https://github.com/login/oauth/authorize",
                TokenEndpoint = "https://github.com/login/oauth/access_token",
                Scopes = "repo read:user"
            }
        }),
        DataProtectionProvider.Create(nameof(GitHubSignInTests)),
        new Factory(handler),
        NullLogger<OAuthFlowService>.Instance);

    [Fact]
    public void GitHub_signs_in_through_its_own_window()
    {
        Assert.True(SourceTokenProvider.SupportsExternalSignIn(SourceType.GitHub));
        Assert.Equal(SourceProvider.GitHub, SourceTokenProvider.ProviderFor(SourceType.GitHub));

        var request = Build(new Answer("{}")).BuildAuthorizeRequest(
            SourceProvider.GitHub, Guid.NewGuid(), Guid.NewGuid(), SourceType.GitHub);

        Assert.StartsWith("https://github.com/login/oauth/authorize", request.Url);
        var query = HttpUtility.ParseQueryString(new Uri(request.Url).Query);
        Assert.Equal("repo read:user", query["scope"]);
        Assert.Null(query["client_secret"]);
    }

    [Fact]
    public async Task A_GitHub_token_never_expires_and_is_asked_for_as_json()
    {
        var handler = new Answer("""{"access_token":"gho_abc","token_type":"bearer","scope":"repo,read:user"}""");
        var service = Build(handler);
        var start = service.BuildAuthorizeRequest(SourceProvider.GitHub, Guid.NewGuid(), Guid.NewGuid(), SourceType.GitHub);

        var (tokens, _, _, type) = await service.CompleteAsync("code", start.State, default);

        Assert.Equal(SourceType.GitHub, type);
        Assert.Equal("gho_abc", tokens.AccessToken);
        // No expires_in: an expiry would mark the connection expired an hour later,
        // with no refresh token to renew it.
        Assert.Null(tokens.ExpiresAt);
        Assert.Contains(handler.Seen!.Headers.Accept, h => h.MediaType == "application/json");
    }

    [Fact]
    public async Task GitHub_reports_a_bad_code_with_200_and_that_is_still_a_failure()
    {
        var service = Build(new Answer("""{"error":"bad_verification_code","error_description":"The code passed is incorrect or expired."}"""));
        var start = service.BuildAuthorizeRequest(SourceProvider.GitHub, Guid.NewGuid(), Guid.NewGuid(), SourceType.GitHub);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteAsync("code", start.State, default));
        Assert.Contains("incorrect or expired", error.Message);
    }

    [Theory]
    [InlineData("not_indexable: vendored or generated directory", true)]
    [InlineData("low_quality_extraction: only 9 characters of text extracted", true)]
    [InlineData("unsupported_format: .doc is a legacy format", true)]
    [InlineData("extraction_failed: docling could not read report.pdf", false)]
    [InlineData("gave up after 3 attempts: embedding_unavailable: timeout", false)]
    public void Files_with_nothing_to_index_are_skipped_not_failed(string error, bool skipped) =>
        Assert.Equal(skipped, IngestionEndpoints.IsSkip(error));
}
