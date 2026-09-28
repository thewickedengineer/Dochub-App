namespace Dochub.Api.Services;

public class AzureStorageOptions
{
    public const string Section = "AzureStorage";
    /// <summary>Full connection string, or "UseDevelopmentStorage=true" for Azurite.</summary>
    public string ConnectionString { get; set; } = "UseDevelopmentStorage=true";
    public string Container { get; set; } = "dochub-documents";
    /// <summary>When set, a managed-identity/DefaultAzureCredential connection is used instead.</summary>
    public string? AccountUrl { get; set; }
}

public class ServiceBusOptions
{
    public const string Section = "ServiceBus";
    public string? ConnectionString { get; set; }
    public string? Namespace { get; set; }
    /// <summary>Queue 1: sources waiting for their files to be moved to blob storage.</summary>
    public string ExtractQueue { get; set; } = "dochub-source-upload-requested";
    /// <summary>Queue 2: uploaded documents waiting to be vectorized.</summary>
    public string ProcessQueue { get; set; } = "dochub-document-process-requested";
    /// <summary>
    /// False routes both queues to a Postgres-backed queue with the same
    /// semantics, so the stack runs end to end with no Azure subscription.
    /// </summary>
    public bool Enabled { get; set; }
}

public class JwtOptions
{
    public const string Section = "Jwt";
    public string Issuer { get; set; } = "dochub-api";
    public string Audience { get; set; } = "dochub-web";
    public string SigningKey { get; set; } = default!;
    public int AccessTokenMinutes { get; set; } = 480;
}

public class SsoOptions
{
    public const string Section = "Sso";
    public string? GoogleClientId { get; set; }
    public string? MicrosoftClientId { get; set; }
    public string MicrosoftTenant { get; set; } = "common";
    /// <summary>
    /// Development escape hatch: accept an unverified identity so the UI can be
    /// driven without registering real Google/Microsoft OAuth apps.
    /// </summary>
    public bool AllowDevSignIn { get; set; }
}

public class PipelineOptions
{
    public const string Section = "Pipeline";
    /// <summary>Where local file bytes wait between staging and extraction. Defaults to the temp directory.</summary>
    public string? StagingPath { get; set; }
}

public class SyncOptions
{
    public const string Section = "Sync";
    /// <summary>
    /// Runs the recurring-sync worker in this process. Set false here and true in
    /// a second deployment to host the sync service on its own.
    /// </summary>
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 60;
    /// <summary>Ceiling on schedules started per sweep, so a backlog drains steadily.</summary>
    public int MaxPerSweep { get; set; } = 5;
    public string DefaultTimeZone { get; set; } = "UTC";
}

public class ExtractorOptions
{
    public const string Section = "Extractor";
    /// <summary>
    /// Runs the extractor in this process. Set false here and true in a second
    /// deployment to host it on its own — it shares only the database and queue.
    /// </summary>
    public bool Enabled { get; set; } = true;
    /// <summary>How long to wait before polling again when the queue is empty.</summary>
    public int IdlePollSeconds { get; set; } = 2;
}

public class VectorServiceOptions
{
    public const string Section = "VectorService";
    /// <summary>
    /// Stands in for the Python vector service by consuming the process queue and
    /// marking documents indexed. Turn this off the moment the real one is running,
    /// or the two will race for the same messages.
    /// </summary>
    public bool SimulateLocally { get; set; }
    public int SimulatedSecondsPerDocument { get; set; } = 2;
}

/// <summary>
/// Delegated OAuth for the document sources. This is separate from
/// <see cref="SsoOptions"/>: that proves who the user is, this asks the user to
/// let Dochub read their Drive or SharePoint on their behalf.
/// </summary>
public class OAuthOptions
{
    public const string Section = "OAuth";

    /// <summary>
    /// Where the provider sends the browser back. Must be registered verbatim with
    /// Google and Microsoft, and must point at the web app, not the API.
    /// </summary>
    public string RedirectUri { get; set; } = "http://localhost:5173/oauth/callback";

    public OAuthProviderOptions Google { get; set; } = new()
    {
        AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth",
        TokenEndpoint = "https://oauth2.googleapis.com/token",
        // Read-only: Dochub never needs to modify anything in someone's Drive.
        Scopes = "openid email https://www.googleapis.com/auth/drive.readonly"
    };

    public OAuthProviderOptions Microsoft { get; set; } = new()
    {
        Tenant = "common",
        // offline_access is what yields a refresh token; without it a scheduled
        // sync stops working an hour after it is set up.
        Scopes = "openid email offline_access Files.Read.All Sites.Read.All"
    };

    public OAuthProviderOptions For(SourceProvider provider) => provider switch
    {
        SourceProvider.Google => Google,
        SourceProvider.Microsoft => Microsoft,
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };
}

public class OAuthProviderOptions
{
    public string? ClientId { get; set; }
    /// <summary>
    /// Optional. With it the code exchange is a confidential client; without it
    /// PKCE alone secures the exchange, which is the right shape for a SPA.
    /// </summary>
    public string? ClientSecret { get; set; }
    public string Tenant { get; set; } = "common";
    public string Scopes { get; set; } = "";
    public string AuthorizeEndpoint { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";

    public string ResolvedAuthorizeEndpoint => string.IsNullOrWhiteSpace(AuthorizeEndpoint)
        ? $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize"
        : AuthorizeEndpoint;

    public string ResolvedTokenEndpoint => string.IsNullOrWhiteSpace(TokenEndpoint)
        ? $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token"
        : TokenEndpoint;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);
}

/// <summary>The identity provider behind a source, as opposed to the source itself.</summary>
public enum SourceProvider { Google, Microsoft }
