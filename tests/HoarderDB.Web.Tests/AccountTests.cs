using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HoarderDB.Web.Tests;

/// <summary>Admin password change. Throwaway Test_&lt;guid&gt; roots only; passwords are test constants.</summary>
public class AccountTests
{
    private const string NewPw = "a brand new passphrase 42";
    private const string Url = "/api/account/password";

    private static string KeyDir(HoarderDbWebFactory f)
    {
        HoarderDbWebFactory.EnsureThrowaway(f.Root);
        return Path.Combine(f.Root, "keys-not-a-db");
    }
    private static string StoreFile(HoarderDbWebFactory f) => Path.Combine(KeyDir(f), "admin-password.json");

    private static Task<HttpResponseMessage> Change(HttpClient c, string cur, string next) =>
        c.PostAsJsonAsync(Url, new { currentPassword = cur, newPassword = next });

    private static async Task<HttpStatusCode> LoginAsync(HoarderDbWebFactory f, string pw)
    {
        var c = f.NewClient();
        return (await c.PostAsJsonAsync("/login", new { username = HoarderDbWebFactory.Username, password = pw })).StatusCode;
    }

    [Fact]
    public async Task Success_ChangesPassword_StoresHashOnly_Mode0600_NoTmpLeft()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Change(c, HoarderDbWebFactory.Password, NewPw)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(f, HoarderDbWebFactory.Password));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f, NewPw));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs")).StatusCode);   // changer stays signed in

        var text = File.ReadAllText(StoreFile(f));
        Assert.DoesNotContain(NewPw, text);
        Assert.DoesNotContain(HoarderDbWebFactory.Password, text);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(StoreFile(f)));
        Assert.Empty(Directory.GetFiles(KeyDir(f), "*.tmp-*"));
    }

    [Fact]
    public async Task WrongCurrent_Is403_AndNothingChanges()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await Change(c, "not the password", NewPw)).StatusCode);
        Assert.False(File.Exists(StoreFile(f)));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f, HoarderDbWebFactory.Password));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs")).StatusCode);   // 403 does not sign out
    }

    [Theory]
    [InlineData("short pw 11")]                                   // 11 chars
    [InlineData("tester")]                                        // username (also too short)
    public async Task RuleViolations_Are400(string next)
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(c, HoarderDbWebFactory.Password, next)).StatusCode);
        Assert.False(File.Exists(StoreFile(f)));
    }

    [Fact]
    public async Task Length_Boundaries_AndSameAsCurrent()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(c, HoarderDbWebFactory.Password, new string('x', 129))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(c, HoarderDbWebFactory.Password, HoarderDbWebFactory.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(c, HoarderDbWebFactory.Password, new string('x', 128))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(c, new string('x', 128), new string('y', 12))).StatusCode);
    }

    [Fact]
    public async Task Malformed_Or_MissingFields_Are400()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync(Url, new StringContent("{not json", Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Url, new { currentPassword = HoarderDbWebFactory.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync(Url, new { newPassword = NewPw })).StatusCode);
    }

    [Fact]
    public async Task NonJson_415_NoCookie_401_ApiKey_403()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["currentPassword"] = HoarderDbWebFactory.Password, ["newPassword"] = NewPw });
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await c.PostAsync(Url, form)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Change(f.NewClient(), HoarderDbWebFactory.Password, NewPw)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Change(f.NewApiKeyClient(), HoarderDbWebFactory.Password, NewPw)).StatusCode);
        Assert.False(File.Exists(StoreFile(f)));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f, HoarderDbWebFactory.Password));
    }

    [Fact]
    public async Task PerIpRateLimit_Gives429()
    {
        using var f = new HoarderDbWebFactory(1000, new() { ["Auth:PasswordChangeAttemptsPerMinute"] = "2" });
        var c = await f.NewLoggedInClientAsync();
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 3; i++) codes.Add((await Change(c, "wrong", NewPw)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[2]);
    }

    [Fact]
    public async Task Lockout_After5Failures_BlocksEvenCorrectPassword()
    {
        using var f = new HoarderDbWebFactory(1000, new() { ["Auth:PasswordChangeAttemptsPerMinute"] = "100" });
        var c = await f.NewLoggedInClientAsync();
        for (int i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Forbidden, (await Change(c, "wrong" + i, NewPw)).StatusCode);
        var r = await Change(c, HoarderDbWebFactory.Password, NewPw);
        Assert.Equal(HttpStatusCode.TooManyRequests, r.StatusCode);
        Assert.True(r.Headers.Contains("Retry-After"));
        Assert.False(File.Exists(StoreFile(f)));
    }

    [Fact]
    public async Task Change_InvalidatesOtherSessions_ButNotApiKey()
    {
        using var f = new HoarderDbWebFactory();
        var a = await f.NewLoggedInClientAsync();
        var b = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Change(a, HoarderDbWebFactory.Password, NewPw)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.GetAsync("/api/dbs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await a.GetAsync("/api/dbs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.NewApiKeyClient().GetAsync("/api/dbs")).StatusCode);
    }

    [Fact]
    public async Task Persists_AcrossRestart_And_StoreWinsWhileEnvUnchanged()
    {
        using var f1 = new HoarderDbWebFactory();
        var c = await f1.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Change(c, HoarderDbWebFactory.Password, NewPw)).StatusCode);

        // "Restart": a new host on the same root with the SAME env hash.
        using var f2 = new HoarderDbWebFactory(1000, null, null, null, null, f1.Root, f1.EnvHash);
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f2, NewPw));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(f2, HoarderDbWebFactory.Password));
    }

    [Fact]
    public async Task RotatedEnvHash_OverridesStaleStore()
    {
        using var f1 = new HoarderDbWebFactory();
        var c = await f1.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await Change(c, HoarderDbWebFactory.Password, NewPw)).StatusCode);

        // Same root, but the operator rotated the env hash (fresh salt => different hash string).
        using var f2 = new HoarderDbWebFactory(1000, null, null, null, null, f1.Root, null);
        Assert.NotEqual(f1.EnvHash, f2.EnvHash);
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f2, HoarderDbWebFactory.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(f2, NewPw));
    }

    [Fact]
    public async Task MissingStore_UsesEnvHash()
    {
        using var f = new HoarderDbWebFactory();
        Assert.False(File.Exists(StoreFile(f)));
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f, HoarderDbWebFactory.Password));
    }

    [Fact]
    public async Task OldCookieWithoutStampClaim_IsRejected()
    {
        // Fail closed: a cookie lacking pwv (pre-feature cookie) must not authenticate. Simulate by
        // forging a ticket with the host's own Data Protection keys but no pwv claim.
        using var f = new HoarderDbWebFactory();
        var c = f.NewClient();
        _ = f.Server;   // start host
        var opt = f.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>().Get("Cookies");
        var identity = new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, HoarderDbWebFactory.Username),
             new System.Security.Claims.Claim("auth_method", "cookie")], "Cookies");
        var ticket = new Microsoft.AspNetCore.Authentication.AuthenticationTicket(
            new System.Security.Claims.ClaimsPrincipal(identity),
            new Microsoft.AspNetCore.Authentication.AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1), IsPersistent = true }, "Cookies");
        var value = opt.TicketDataFormat.Protect(ticket);
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/dbs");
        req.Headers.Add("Cookie", "hoarderdb.sid=" + value);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task WriteFailure_Is500_AndOldPasswordStillValid()
    {
        if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess) return;   // chmod is not enforced for root
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var dir = KeyDir(f);
        try
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            Assert.Equal(HttpStatusCode.InternalServerError, (await Change(c, HoarderDbWebFactory.Password, NewPw)).StatusCode);
        }
        finally { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Equal(HttpStatusCode.NoContent, await LoginAsync(f, HoarderDbWebFactory.Password));
        Assert.Equal(HttpStatusCode.Unauthorized, await LoginAsync(f, NewPw));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp-*"));
    }

    [Fact]
    public async Task SettingsPage_RequiresAuth()
    {
        using var f = new HoarderDbWebFactory();
        var anon = f.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync("/settings.html")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync("/settings.js")).StatusCode);
        var c = await f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/settings.js")).StatusCode);
    }

    [Fact]
    public async Task Passwords_AreNeverLogged()
    {
        var sink = new CaptureProvider();
        using var f0 = new HoarderDbWebFactory();
        using var f = f0.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(sink))));
        var c = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        Assert.Equal(HttpStatusCode.NoContent,
            (await c.PostAsJsonAsync("/login", new { username = HoarderDbWebFactory.Username, password = HoarderDbWebFactory.Password })).StatusCode);
        await Change(c, "wrong-current-secret", NewPw);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(c, HoarderDbWebFactory.Password, NewPw)).StatusCode);
        var all = sink.Text();
        Assert.Contains("Admin password changed", all);
        foreach (var secret in new[] { NewPw, HoarderDbWebFactory.Password, "wrong-current-secret" })
            Assert.DoesNotContain(secret, all);
    }

    private sealed class CaptureProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();
        public string Text() => string.Join("\n", _lines);
        public ILogger CreateLogger(string categoryName) => new L(_lines);
        public void Dispose() { }
        private sealed class L(System.Collections.Concurrent.ConcurrentQueue<string> q) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? ex, Func<TState, Exception?, string> fmt) =>
                q.Enqueue(fmt(state, ex) + (ex?.ToString() ?? ""));
        }
    }

    // Child-process pattern (see Startup_FailsFast_WithoutCredentials): in-process host start failures are flaky.
    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("{\"v\":2,\"hash\":\"x\",\"envFp\":\"y\",\"stamp\":\"z\"}")]
    [InlineData("{\"v\":1,\"hash\":\"not-a-hash\",\"envFp\":\"y\",\"stamp\":\"z\"}")]
    public async Task CorruptStore_FailsStartup_WithoutLeakingContents(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        try
        {
            var keys = Path.Combine(root, "keys");
            Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "admin-password.json"), content);
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add(typeof(Program).Assembly.Location);
            foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("HOARDERDB_", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(k);
            psi.Environment["HOARDERDB_Storage__DataRoot"] = root;
            psi.Environment["HOARDERDB_Storage__KeyPath"] = keys;
            psi.Environment["HOARDERDB_Auth__Username"] = HoarderDbWebFactory.Username;
            psi.Environment["HOARDERDB_Auth__PasswordHash"] = new PasswordHasher<string>().HashPassword(HoarderDbWebFactory.Username, HoarderDbWebFactory.Password);
            psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = Task.WhenAll(proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { proc.Kill(entireProcessTree: true); throw; }
            var text = string.Join("\n", await output);

            Assert.NotEqual(0, proc.ExitCode);
            Assert.Contains("admin password file", text);
            Assert.Contains("admin-password.json", text);
            Assert.DoesNotContain("not-a-hash", text);
        }
        finally { Directory.Delete(root, true); }
    }
}
