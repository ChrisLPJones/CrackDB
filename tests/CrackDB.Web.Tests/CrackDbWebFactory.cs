using System.Net;
using System.Net.Http.Json;
using System.Text;
using CrackDB;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CrackDB.Web.Tests;

/// <summary>
/// Hosts the web app against a throwaway data root Test_&lt;guid&gt; under the OS temp dir. Databases are
/// seeded through the engine and CLOSED before the host starts (RocksDB: one opener per directory).
/// Refuses any root that is not provably throwaway; never touches real data.
/// </summary>
public class CrackDbWebFactory : WebApplicationFactory<Program>
{
    public const string Username = "tester";
    public const string Password = "correct horse battery staple";
    public const string ApiKey = "test-api-key-0123456789";
    public const string DbName = "Test_words";
    public static readonly string[] Words = { "abc", "ab", "zz", "hunter2", "abcd", "b", "longerword", "xyzzy1", "pass1234" };

    public string Root { get; }
    private readonly int _loginLimit;

    public CrackDbWebFactory() : this(1000) { }

    internal CrackDbWebFactory(int loginLimit)
    {
        _loginLimit = loginLimit;
        Root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        EnsureThrowaway(Root);
        Directory.CreateDirectory(Root);
        Seed(Path.Combine(Root, DbName), Words);
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
            ["Storage:KeyPath"] = Path.Combine(Root, "keys-not-a-db"),
            ["Auth:Username"] = Username,
            ["Auth:PasswordHash"] = new PasswordHasher<string>().HashPassword(Username, Password),
            ["Auth:ApiKeyHash"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ApiKey))),
            ["Auth:LoginAttemptsPerMinute"] = _loginLimit.ToString(),
        }));
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
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
