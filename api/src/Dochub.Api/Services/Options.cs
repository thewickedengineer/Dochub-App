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

    /// <summary>
    /// The client id an ID token's audience must match. Leave empty to reuse
    /// <c>OAuth:Google:ClientId</c> — the usual case, where one app registration
    /// both signs people in and reads their documents.
    /// </summary>
    public string? GoogleClientId { get; set; }

    /// <summary>Same idea; falls back to <c>OAuth:Microsoft:ClientId</c>.</summary>
    public string? MicrosoftClientId { get; set; }

    /// <summary>Falls back to <c>OAuth:Microsoft:Tenant</c>.</summary>
    public string? MicrosoftTenant { get; set; }

    /// <summary>
    /// Development escape hatch: accept an unverified identity so the UI can be
    /// driven without registering real Google/Microsoft OAuth apps.
    /// </summary>
    public bool AllowDevSignIn { get; set; }

    /// <summary>
    /// Resolves each setting against the OAuth section, so a single registration
    /// needs configuring once rather than in two places that must agree.
    /// </summary>
    public SsoOptions ResolvedAgainst(OAuthOptions oauth) => new()
    {
        GoogleClientId = Pick(GoogleClientId, oauth.Google.ClientId),
        MicrosoftClientId = Pick(MicrosoftClientId, oauth.Microsoft.ClientId),
        MicrosoftTenant = Pick(MicrosoftTenant, oauth.Microsoft.Tenant) ?? "common",
        AllowDevSignIn = AllowDevSignIn
    };

    private static string? Pick(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback) : preferred;
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

public class IngestionOptions
{
    public const string Section = "Ingestion";

    /// <summary>
    /// Shared secret the vector service presents on its callbacks, in the
    /// X-Dochub-Service-Key header. Those calls come from a service, not a
    /// signed-in person, so they cannot use the user JWT.
    /// </summary>
    public string? ServiceKey { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceKey) && ServiceKey!.Length >= 32;
}


public class RagOptions
{
    public const string Section = "Rag";

    /// <summary>The RAG platform's API (rag-ingest api). Calls carry Ingestion:ServiceKey.</summary>
    public string BaseUrl { get; set; } = "http://localhost:8090";

    /// <summary>Upper bound on a purge call when an upload is removed.</summary>
    public int PurgeTimeoutSeconds { get; set; } = 60;

    /// <summary>Upper bound on one chat answer, retrieval and generation together.</summary>
    public int TimeoutSeconds { get; set; } = 180;

    /// <summary>Earlier turns sent with each question, so follow-ups can be understood.</summary>
    public int HistoryMessages { get; set; } = 12;
}
