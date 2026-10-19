using System.Net;
using System.Threading.RateLimiting;
using CrackDB.Web.Auth;
using CrackDB.Web.Endpoints;
using CrackDB.Web.Jobs;
using CrackDB.Web.Options;
using CrackDB.Web.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// CrackDB web host: read-only API + static UI over the engine, behind single-user auth.
// Caddy terminates TLS and is the only client of Kestrel (loopback). Config comes from
// appsettings + CRACKDB_-prefixed environment variables (e.g. CRACKDB_Auth__PasswordHash).
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("CRACKDB_");

// Loopback only by default; override with --urls / ASPNETCORE_URLS.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
    builder.WebHost.UseUrls("http://127.0.0.1:5080");
builder.WebHost.ConfigureKestrel(k => { k.AddServerHeader = false; k.Limits.MaxRequestBodySize = 64 * 1024; });

var services = builder.Services;

// Options: validated at startup so missing credentials/paths abort boot with a clear message.
services.AddOptions<StorageOptions>().BindConfiguration(StorageOptions.Section).ValidateOnStart();
services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateOnStart();
services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
services.AddSingleton<IValidateOptions<AuthOptions>, AuthOptionsValidator>();
services.AddOptions<UploadOptions>().BindConfiguration(UploadOptions.Section).ValidateOnStart();
services.AddSingleton<IValidateOptions<UploadOptions>, UploadOptionsValidator>();
services.AddOptions<CompactionOptions>().BindConfiguration(CompactionOptions.Section).ValidateOnStart();
services.AddSingleton<IValidateOptions<CompactionOptions>, CompactionOptionsValidator>();

// Storage
services.AddSingleton<DataRootResolver>();
services.AddSingleton<DbManager>();
services.AddSingleton<IDbManager>(sp => sp.GetRequiredService<DbManager>());
services.AddHostedService(sp => sp.GetRequiredService<DbManager>());   // StopAsync closes every handle

// Background jobs (P3). The job worker is registered AFTER DbManager on purpose: hosted services stop
// in reverse order, so the worker stops (cancels the running import, reopens Read, flushes) first and
// only then does DbManager close the handles.
services.AddSingleton<IncomingPathResolver>();
services.TryAddSingleton<IDiskSpace, DriveDiskSpace>();
services.AddSingleton<IImportHistory, ImportHistory>();
services.AddSingleton<JobRegistry>();
services.TryAddSingleton<IImportProgressHook, NoopImportProgressHook>();
services.AddSingleton<JobQueue>();
services.AddHostedService(sp => sp.GetRequiredService<JobQueue>());
// Registered after JobQueue so it stops first on shutdown (reverse order).
services.AddHostedService<WeeklyCompactionService>();

// Data Protection keys persisted so sessions survive restarts (key dir must be 0700, owner crackdb).
// Configured lazily from options so the path is read after all configuration sources are in.
services.AddDataProtection().SetApplicationName("crackdb-web");
services.AddOptions<KeyManagementOptions>().Configure<IOptions<StorageOptions>, ILoggerFactory>((k, storage, lf) =>
{
    var dir = Directory.CreateDirectory(storage.Value.KeyPath);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    k.XmlRepository = new FileSystemXmlRepository(dir, lf);
});

// Auth: cookie for the browser, X-API-Key for scripts; a policy scheme picks per request.
services.AddSingleton<CredentialVerifier>();
services.AddAuthentication(CredentialVerifier.SchemeSmart)
    .AddPolicyScheme(CredentialVerifier.SchemeSmart, null, o =>
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey(CredentialVerifier.ApiKeyHeader)
                ? CredentialVerifier.SchemeApiKey
                : CredentialVerifier.SchemeCookie)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(CredentialVerifier.SchemeApiKey, null)
    .AddCookie(CredentialVerifier.SchemeCookie, o =>
    {
        o.Cookie.Name = "crackdb.sid";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;   // the CSRF defence for the form-POST export
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.LoginPath = "/login.html";
        // API callers get 401, not a redirect to the login page.
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser().Build());

// Login rate limit: fixed window per client IP (the real LAN IP, via ForwardedHeaders).
services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy(AuthEndpoints.LoginLimiter, ctx =>
    {
        var permits = ctx.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginAttemptsPerMinute;
        return RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permits,
                Window = TimeSpan.FromSeconds(60),
                QueueLimit = 0,
            });
    });
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new ProblemDetails { Status = 429, Title = "Too many login attempts." }, cancellationToken: ct);
    };
});

// Only loopback plus explicitly configured proxies/networks (CRACKDB_ForwardedProxies /
// CRACKDB_ForwardedNetworks) may supply X-Forwarded-*. Malformed values abort startup.
services.Configure<ForwardedHeadersOptions>(o => ForwardedOptions.Apply(o, builder.Configuration));

services.AddProblemDetails();
services.AddExceptionHandler<ApiExceptionHandler>();

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Content-Security-Policy"] = "default-src 'self'; frame-ancestors 'none'; form-action 'self'";
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});

app.UseRateLimiter();
app.UseAuthentication();

// Static files are served before endpoint routing, so the fallback policy would not cover the app
// shell. Gate everything except the explicit anonymous list here.
var anonymous = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "/healthz", "/login", "/logout", "/login.html", "/login.js", "/style.css" };
app.Use(async (ctx, next) =>
{
    if (!anonymous.Contains(ctx.Request.Path.Value ?? "") && ctx.User.Identity?.IsAuthenticated != true)
    {
        await ctx.ChallengeAsync();
        return;
    }
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();
app.MapAuth();
app.MapApi();
app.MapJobs();

app.Run();

/// <summary>Maps domain exceptions to RFC 7807 problems so handlers stay thin.</summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> log) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            InvalidDbNameException => (400, "Invalid database name."),
            DbNotFoundException => (404, "Database not found."),
            DbBusyException => (409, "Database is busy (import or compact in progress)."),
            DbAlreadyExistsException => (409, "Database already exists."),
            DbLimitReachedException => (409, "Database limit reached."),
            InvalidSourcePathException => (400, "Invalid source path."),
            SourceNotFoundException => (404, "Source file not found."),
            JobNotFoundException => (404, "Job not found."),
            IncomingNotConfiguredException => (404, "Server-side import is not configured."),
            UploadTooLargeException => (413, "Upload exceeds the size limit."),
            BadHttpRequestException b => (b.StatusCode, "Bad request."),
            OperationCanceledException when ctx.RequestAborted.IsCancellationRequested => (499, "Client closed request."),
            _ => (500, "An unexpected error occurred."),
        };
        if (status == 500) log.LogError(ex, "Unhandled exception");
        ctx.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
        });
    }
}

public partial class Program;
