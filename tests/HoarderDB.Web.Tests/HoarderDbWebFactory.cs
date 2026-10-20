using System.Net;
using System.Net.Http.Json;
using System.Text;
using HoarderDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HoarderDB.Web.Tests;

/// <summary>
/// Hosts the web app against a throwaway data root Test_&lt;guid&gt; under the OS temp dir. Databases are
/// seeded through the engine and CLOSED before the host starts (RocksDB: one opener per directory).
/// Refuses any root that is not provably throwaway; never touches real data.
/// </summary>
public class HoarderDbWebFactory : WebApplicationFactory<Program>
{
    public const string Username = "tester";
    public const string Password = "correct horse battery staple";
    public const string ApiKey = "test-api-key-0123456789";
    public const string DbName = "Test_words";
    public static readonly string[] Words = { "abc", "ab", "zz", "hunter2", "abcd", "b", "longerword", "xyzzy1", "pass1234" };

    public string Root { get; }
    /// <summary>Extra service registrations (e.g. a fake IDiskSpace); set before the host is first used.</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }
    /// <summary>Throwaway incoming dir (Test_&lt;guid&gt; under the OS temp dir), separate from the data root.</summary>
    public string Incoming { get; }
    private readonly HoarderDB.Web.Jobs.IImportProgressHook? _hook;
    private readonly HoarderDB.Web.Jobs.ISearchProgressHook? _searchHook;
    private readonly int _loginLimit;
    private readonly Dictionary<string, string?> _extra;
    private readonly string? _remoteIp;
    private readonly bool _ownsRoot;
    /// <summary>The env (bootstrap) password hash; pass it to a second factory to simulate a restart with unchanged env.</summary>
    public string EnvHash { get; }

    public HoarderDbWebFactory() : this(1000) { }

    /// <param name="extraConfig">Extra configuration keys (e.g. ForwardedProxies).</param>
    /// <param name="remoteIp">If set, every request appears to come from this peer address (TestServer has none).</param>
    internal HoarderDbWebFactory(int loginLimit, Dictionary<string, string?>? extraConfig = null, string? remoteIp = null,
        HoarderDB.Web.Jobs.IImportProgressHook? hook = null, HoarderDB.Web.Jobs.ISearchProgressHook? searchHook = null,
        string? existingRoot = null, string? envHash = null)
    {
        EnvHash = envHash ?? new PasswordHasher<string>().HashPassword(Username, Password);
        _ownsRoot = existingRoot is null;
        _hook = hook;
        _searchHook = searchHook;
        _loginLimit = loginLimit;
        _extra = extraConfig ?? new();
        _remoteIp = remoteIp;
        Root = existingRoot ?? Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        EnsureThrowaway(Root);
        Directory.CreateDirectory(Root);
        Incoming = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        EnsureThrowaway(Incoming);
        Directory.CreateDirectory(Incoming);
        if (_ownsRoot) Seed(Path.Combine(Root, DbName), Words);
    }

    public static void EnsureThrowaway(string path)
    {
        var full = Path.GetFullPath(path);
        var tmp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        var dir = Path.GetDirectoryName(full)!.TrimEnd(Path.DirectorySeparatorChar);
        if (!Path.GetFileName(full).StartsWith("Test_", StringComparison.Ordinal) || dir != tmp)
            throw new InvalidOperationException($"Refusing to use non-throwaway path: {path}");
    }

    public static void Seed(string dbPath, IEnumerable<string> words)
    {
        EnsureThrowaway(Path.GetDirectoryName(dbPath)!);
        using var db = new WordlistDatabase(dbPath, new UTF8Encoding(false));
        db.Import(words, null, null, threads: 2);
    }   // disposed here: closed before the host opens it

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:DataRoot"] = Root,
            ["Storage:IncomingRoot"] = Incoming,
            ["Storage:KeyPath"] = Path.Combine(Root, "keys-not-a-db"),
            // Fixed, small budget rather than "auto": auto derives from GC.GetGCMemoryInfo's reported
            // total available memory, which some sandboxed/nested dev or CI hosts over-report (observed:
            // sysconf(_SC_PHYS_PAGES) reflecting the outer host rather than this environment's real,
            // cgroup-visible RAM). That mismatch doesn't affect production (ImportMemoryMb is always
            // configured there), but it would make the headroom backstop (HeadroomGuard) spuriously
            // refuse every job in tests if left on auto. A test can still override via extraConfig.
            ["Storage:ImportMemoryMb"] = "256",
            ["Auth:Username"] = Username,
            ["Auth:PasswordHash"] = EnvHash,
            ["Auth:ApiKeyHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey))),
            ["Auth:LoginAttemptsPerMinute"] = _loginLimit.ToString(),
        }.Concat(_extra)));
        if (ExtraServices is not null) builder.ConfigureServices(ExtraServices);
        if (_hook is not null)
            builder.ConfigureServices(s => s.AddSingleton(_hook));
        if (_searchHook is not null)
            builder.ConfigureServices(s => s.AddSingleton(_searchHook));
        if (_remoteIp is not null)
        {
            var ip = IPAddress.Parse(_remoteIp);
            builder.ConfigureServices(s => s.AddTransient<IStartupFilter>(_ => new RemoteIpFilter(ip)));
        }
    }

    private sealed class RemoteIpFilter(IPAddress ip) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, n) => { ctx.Connection.RemoteIpAddress = ip; return n(ctx); });
            next(app);
        };
    }

    /// <summary>https base address so the Secure session cookie round-trips.</summary>
    public HttpClient NewClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false,
        HandleCookies = true,
    });

    public HttpClient NewApiKeyClient()
    {
        var c = NewClient();
        c.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        return c;
    }

    public async Task<HttpClient> NewLoggedInClientAsync()
    {
        var c = NewClient();
        var r = await c.PostAsJsonAsync("/login", new { username = Username, password = Password });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return c;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);   // host stops first, closing its DB handles
        if (disposing)
        {
            EnsureThrowaway(Root);
            if (_ownsRoot && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
            EnsureThrowaway(Incoming);
            if (Directory.Exists(Incoming)) Directory.Delete(Incoming, recursive: true);
        }
    }
}
