namespace Dochub.Api.Services;

public static class CorsOrigins
{
    /// <summary>
    /// True when an origin is this machine. Used only in development, where the
    /// dev server's port moves whenever 5173 is taken and people reach it as
    /// either localhost or 127.0.0.1.
    ///
    /// Matching is on the parsed host, never a string prefix: "localhost.evil.com"
    /// and "http://evil.com/#localhost" must not pass.
    /// </summary>
    public static bool IsLoopback(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        return uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }
}
