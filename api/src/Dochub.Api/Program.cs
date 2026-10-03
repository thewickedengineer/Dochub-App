using System.Text;
using System.Text.Json.Serialization;
using Dochub.Api.Auth;
using Dochub.Api.Contracts;
using Dochub.Api.Data;
using Dochub.Api.Endpoints;
using Dochub.Api.Hubs;
using Dochub.Api.Services;
using Dochub.Api.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);

// ── Configuration ─────────────────────────────────────────────────────────────
builder.Services.Configure<AzureStorageOptions>(builder.Configuration.GetSection(AzureStorageOptions.Section));
builder.Services.Configure<ServiceBusOptions>(builder.Configuration.GetSection(ServiceBusOptions.Section));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.Section));
builder.Services.Configure<SsoOptions>(builder.Configuration.GetSection(SsoOptions.Section));
builder.Services.Configure<OAuthOptions>(builder.Configuration.GetSection(OAuthOptions.Section));
builder.Services.Configure<PipelineOptions>(builder.Configuration.GetSection(PipelineOptions.Section));
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.Section));
builder.Services.Configure<ExtractorOptions>(builder.Configuration.GetSection(ExtractorOptions.Section));
builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection(IngestionOptions.Section));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection(RagOptions.Section));

// Fail here rather than at the first click: a bad reply address surfaces from the
// provider as an opaque code (AADSTS900971) that points nowhere near the cause.
var oauthOptions = builder.Configuration.GetSection(OAuthOptions.Section).Get<OAuthOptions>() ?? new OAuthOptions();
if (oauthOptions.Google.IsConfigured || oauthOptions.Microsoft.IsConfigured)
{
    OAuthFlowService.RequireRedirectUri(oauthOptions.RedirectUri);

    // Microsoft's "Web" platform is a confidential client: the code exchange is
    // refused without a secret. Its "SPA" platform rejects a server-side exchange
    // outright, so Web plus a secret is the registration this flow needs.
    if (oauthOptions.Microsoft.IsConfigured && string.IsNullOrWhiteSpace(oauthOptions.Microsoft.ClientSecret))
        Console.WriteLine(
            "[warn] OAuth:Microsoft:ClientSecret is empty. Dochub exchanges the code server-side, so an " +
            "Entra app registered under the Web platform will reject it. Add the secret, or register the " +
            "redirect URI as a Mobile/desktop (public client) platform instead.");
}

var jwt = builder.Configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
    throw new InvalidOperationException(
        "Jwt:SigningKey must be set to at least 32 characters. Set it via user-secrets or the DOCHUB_JWT_KEY environment variable.");

// ── Data ──────────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<DochubDbContext>(options =>
    options.UseNpgsql(
        builder.Configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured."),
        npgsql =>
        {
            npgsql.EnableRetryOnFailure(3);
            // Pin the history table to the same schema as everything else, so it is
            // never resolved differently between the first run and later ones.
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "dochub");
        }));

// ── Auth ──────────────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };

        // SignalR cannot set an Authorization header on the WebSocket handshake,
        // so the hub reads the token from the query string instead.
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var token = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && context.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddDataProtection();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<IAccessTokenService, AccessTokenService>();
builder.Services.AddSingleton<ISsoValidator, SsoValidator>();
builder.Services.AddSingleton<ITokenProtector, TokenProtector>();
builder.Services.AddSingleton<IOAuthFlowService, OAuthFlowService>();
builder.Services.AddSingleton<IShareLinkResolver, ShareLinkResolver>();
builder.Services.AddSingleton<ISourceBrowser, SourceBrowser>();
builder.Services.AddScoped<ISourceTokenProvider, SourceTokenProvider>();
builder.Services.AddSingleton<IBlobStorageService, BlobStorageService>();

// One queue abstraction, two backings: a real Service Bus namespace when one is
// configured, and a Postgres-backed queue with the same semantics when not.
var serviceBusOptions = builder.Configuration.GetSection(ServiceBusOptions.Section).Get<ServiceBusOptions>() ?? new();
if (serviceBusOptions.Enabled)
    builder.Services.AddSingleton<IQueueClient, ServiceBusQueueClient>();
else
    builder.Services.AddSingleton<IQueueClient, DatabaseQueueClient>();
builder.Services.AddSingleton<IStagingStore, FileSystemStagingStore>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddScoped<ISourceSubmissionService, SourceSubmissionService>();
builder.Services.AddScoped<IExtractorService, ExtractorService>();
builder.Services.AddScoped<IDocumentSyncService, DocumentSyncService>();

builder.Services.AddSingleton<ISourceExtractor, LocalFileExtractor>();
builder.Services.AddSingleton<ISourceExtractor, GitHubExtractor>();
builder.Services.AddSingleton<ISourceExtractor, SharePointExtractor>();
builder.Services.AddSingleton<ISourceExtractor, GoogleDriveExtractor>();
builder.Services.AddSingleton<ISourceExtractor, AzureDevOpsExtractor>();
builder.Services.AddSingleton<ISourceExtractorFactory, SourceExtractorFactory>();

builder.Services.AddHostedService<ExtractorWorker>();
builder.Services.AddHostedService<RecurringSyncWorker>();

builder.Services.AddHttpClient("github", c =>
{
    c.BaseAddress = new Uri("https://api.github.com/");
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Dochub/1.0");
    c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.raw+json");
});
builder.Services.AddHttpClient("graph", c => c.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/"));
builder.Services.AddHttpClient("gdrive", c => c.BaseAddress = new Uri("https://www.googleapis.com/drive/v3/"));
builder.Services.AddHttpClient("azdo", c => c.BaseAddress = new Uri("https://dev.azure.com/"));
builder.Services.AddHttpClient("oauth");
// No client-side timeout: an answer streams for as long as Rag:TimeoutSeconds allows.
builder.Services.AddHttpClient("rag", (sp, c) =>
{
    var baseUrl = sp.GetRequiredService<IOptions<RagOptions>>().Value.BaseUrl.TrimEnd('/') + "/";
    c.BaseAddress = new Uri(baseUrl);
    c.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<IRagChatClient, RagChatClient>();
builder.Services.AddSingleton<IRagPurgeClient, RagPurgeClient>();
builder.Services.AddScoped<UploadRemovalService>();
builder.Services.AddScoped<ChatService>();

builder.Services.AddSignalR();
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var corsOrigins = builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? [];
if (!builder.Environment.IsDevelopment() && corsOrigins.Length == 0)
    throw new InvalidOperationException(
        "Cors:Origins must list the sites allowed to call this API. Leaving it empty outside " +
        "development would block every browser client.");

builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    policy.AllowAnyHeader().AllowAnyMethod().AllowCredentials()
        // SignalR reads this to decide whether it may use a streaming transport.
        .WithExposedHeaders("Content-Disposition");

    if (builder.Environment.IsDevelopment())
    {
        // The dev server moves to another port whenever 5173 is taken, and people
        // reach it as either localhost or 127.0.0.1. Both are the same machine, so
        // accept any loopback origin rather than fail with an opaque CORS error.
        policy.SetIsOriginAllowed(CorsOrigins.IsLoopback);
    }
    else
    {
        policy.WithOrigins(corsOrigins);
    }
}));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Dochub API",
        Version = "v1",
        Description = "Organizational RAG document hub — SSO, org/team/group/artifact structure, multi-source ingestion into Azure Blob storage, and Service Bus-driven processing."
    });
    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Token from POST /api/auth/sso."
    });
    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("bearer")] = []
    });
});

builder.Services.AddHealthChecks().AddDbContextCheck<DochubDbContext>("postgres");

var app = builder.Build();

// ── Pipeline ──────────────────────────────────────────────────────────────────
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var feature = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>();
    var exception = feature?.Error;

    // Authorization helpers throw rather than thread a result back up; translate here.
    var (status, code) = exception switch
    {
        UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "unauthorized"),
        ArgumentException => (StatusCodes.Status400BadRequest, "invalid_request"),
        NotSupportedException => (StatusCodes.Status400BadRequest, "unsupported"),
        InvalidOperationException => (StatusCodes.Status409Conflict, "invalid_state"),
        _ => (StatusCodes.Status500InternalServerError, "server_error")
    };

    app.Logger.LogError(exception, "Unhandled {Code} on {Method} {Path}", code, context.Request.Method, context.Request.Path);
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new ApiError(code,
        status == StatusCodes.Status500InternalServerError && !app.Environment.IsDevelopment()
            ? "Something went wrong. The failure has been logged."
            : exception?.Message ?? "Unknown error."));
}));

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "Dochub API v1");
    options.DocumentTitle = "Dochub API";
});

// The root would otherwise 404, which reads as "the API isn't running".
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

app.MapAuthEndpoints();
app.MapOrganizationEndpoints();
app.MapWorkspaceEndpoints();
app.MapProcessEndpoints();
app.MapConnectionEndpoints();
app.MapNotificationEndpoints();
app.MapSyncScheduleEndpoints();
app.MapIngestionEndpoints();
app.MapChatEndpoints();
app.MapPlatformEndpoints();
app.MapHub<NotificationHub>("/hubs/notifications");
app.MapHealthChecks("/health");

// Applies migrations, and seeds a demo organization only when explicitly asked.
await DatabaseInitializer.InitializeAsync(app);

// Say plainly where to look, so a successful start is obvious in the terminal.
app.Lifetime.ApplicationStarted.Register(() =>
{
    var address = app.Urls.FirstOrDefault() ?? "http://localhost:5080";
    app.Logger.LogInformation("Dochub API ready — Swagger UI at {Swagger}, OpenAPI at {Spec}, health at {Health}",
        $"{address}/swagger", $"{address}/swagger/v1/swagger.json", $"{address}/health");

    // Printed in brackets so a stray space or trailing slash is visible: this
    // string must match a registered redirect URI byte for byte.
    if (oauthOptions.Google.IsConfigured || oauthOptions.Microsoft.IsConfigured)
        app.Logger.LogInformation(
            "OAuth reply address is [{RedirectUri}] — register exactly this, with no trailing slash",
            oauthOptions.RedirectUri);

    // Which ways in actually work, so a missing client id shows up here rather
    // than at the end of somebody's sign-in.
    var sso = app.Services.GetRequiredService<IOptions<SsoOptions>>().Value.ResolvedAgainst(oauthOptions);
    var methods = new List<string>();
    if (!string.IsNullOrWhiteSpace(sso.GoogleClientId)) methods.Add("Google");
    if (!string.IsNullOrWhiteSpace(sso.MicrosoftClientId)) methods.Add($"Microsoft (tenant {sso.MicrosoftTenant})");
    if (sso.AllowDevSignIn) methods.Add("dev sign-in");

    if (methods.Count == 0)
        app.Logger.LogWarning(
            "No way to sign in: set Sso:GoogleClientId or Sso:MicrosoftClientId (or the matching " +
            "OAuth client id), or turn on Sso:AllowDevSignIn for local work.");
    else
        app.Logger.LogInformation("Sign-in available via {Methods}", string.Join(", ", methods));
});

app.Run();
