using Dochub.Api.Services;

namespace Dochub.Api.Tests;

/// <summary>
/// The development CORS policy trusts this check, so a spoofed origin getting
/// through would hand a hostile page credentialed access to a developer's API.
/// </summary>
public class CorsOriginTests
{
    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:5176")]   // Vite moved ports
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("https://localhost:5173")]
    [InlineData("http://[::1]:5173")]
    [InlineData("http://LOCALHOST:5173")]
    public void Loopback_origins_are_allowed(string origin) =>
        Assert.True(CorsOrigins.IsLoopback(origin));

    [Theory]
    [InlineData("https://evil.example.com")]
    [InlineData("http://localhost.evil.com")]        // suffix, not localhost
    [InlineData("http://notlocalhost")]              // prefix trick
    [InlineData("http://evil.com/#localhost")]       // fragment trick
    [InlineData("http://evil.com?x=localhost")]      // query trick
    [InlineData("http://127.0.0.1.evil.com")]        // looks like loopback
    public void Everything_else_is_rejected(string origin) =>
        Assert.False(CorsOrigins.IsLoopback(origin));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("localhost:5173")]                   // no scheme, not an origin
    [InlineData("file://localhost/etc/passwd")]      // non-http scheme
    [InlineData("javascript:alert(1)")]
    public void Malformed_and_non_http_origins_are_rejected(string? origin) =>
        Assert.False(CorsOrigins.IsLoopback(origin));
}
