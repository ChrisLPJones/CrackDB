using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrackDB.Web.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CrackDB.Web.Tests;

public class ApiTests : IClassFixture<CrackDbWebFactory>
{
    private readonly CrackDbWebFactory _f;
    public ApiTests(CrackDbWebFactory f) => _f = f;

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    // Expected export order is the engine's own: length, then password (ordinal bytes).
    private static List<string> Expected(int? min, int? max, long? limit)
    {
        using var t = new ExpectedDb();
        return t.Db.Range(min, max, limit).ToList();
    }

    private sealed class ExpectedDb : IDisposable
    {
        private readonly string _p = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        public CrackDB.WordlistDatabase Db { get; }
        public ExpectedDb()
        {
            CrackDbWebFactory.EnsureThrowaway(_p);
            Db = new CrackDB.WordlistDatabase(_p, new UTF8Encoding(false));
            Db.Import(CrackDbWebFactory.Words, null, null, threads: 2);
        }
        public void Dispose() { Db.Dispose(); Directory.Delete(_p, true); }
    }

    // ---- auth ----

    [Fact]
    public async Task Healthz_IsAnonymous()
    {
        var r = await _f.NewClient().GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("ok", await r.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/dbs")]
    [InlineData("/api/dbs/Test_words/stats")]
    [InlineData("/api/dbs/Test_words/lookup?word=abc")]
    public async Task Api_WithoutCredentials_Is401Problem(string url)
    {
        var r = await _f.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Contains("problem+json", r.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Export_WithoutCredentials_Is401()
    {
        var r = await _f.NewClient().PostAsync("/api/dbs/Test_words/export", null);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Api_WithValidApiKey_Is200()
    {
        var r = await _f.NewApiKeyClient().GetAsync("/api/dbs");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task Api_WithWrongApiKey_Is401()
    {
        var c = _f.NewClient();
        c.DefaultRequestHeaders.Add("X-API-Key", "nope");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/dbs")).StatusCode);
    }

    [Fact]
    public async Task Login_SetsHardenedCookie_AndGrantsAccess()
    {
        var c = _f.NewClient();
        var r = await c.PostAsJsonAsync("/login", new { username = CrackDbWebFactory.Username, password = CrackDbWebFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var cookie = Assert.Single(r.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs")).StatusCode);
    }

    [Theory]
    [InlineData("tester", "wrong")]
    [InlineData("other", CrackDbWebFactory.Password)]
    [InlineData("", "")]
    public async Task Login_BadCredentials_Is401_NoCookie(string user, string pass)
    {
        var c = _f.NewClient();
        var r = await c.PostAsJsonAsync("/login", new { username = user, password = pass });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(r.Headers.Contains("Set-Cookie"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/dbs")).StatusCode);
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        var c = await _f.NewLoggedInClientAsync();
        await c.PostAsync("/logout", null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/dbs")).StatusCode);
    }

    [Fact]
    public async Task StaticShell_RequiresAuth_ButLoginPageIsOpen()
    {
        var c = _f.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/app.js")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/login.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/style.css")).StatusCode);
        var authed = await _f.NewLoggedInClientAsync();
        Assert.Equal(HttpStatusCode.OK, (await authed.GetAsync("/")).StatusCode);
    }

    [Fact]
    public async Task Login_IsRateLimited()
    {
        using var f = new CrackDbWebFactory(loginLimit: 3);
        var c = f.NewClient();
        var codes = new List<HttpStatusCode>();
        for (int i = 0; i < 5; i++)
            codes.Add((await c.PostAsJsonAsync("/login", new { username = "x", password = "y" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, codes[0]);
        Assert.Equal(HttpStatusCode.TooManyRequests, codes[4]);
        var r = await c.PostAsJsonAsync("/login", new { username = "x", password = "y" });
        Assert.True(r.Headers.Contains("Retry-After"));
    }

    // ---- sandbox ----

    [Theory]
    [InlineData("..%2f..%2fetc")]
    [InlineData("..%5cwindows")]
    [InlineData("%2fetc%2fpasswd")]
    [InlineData("bad%20name")]
    [InlineData("a%3ab")]
    public async Task Sandbox_RejectsTraversalNames_With400(string name)
    {
        var c = _f.NewApiKeyClient();
        var r = await c.GetAsync($"/api/dbs/{name}/stats");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Sandbox_UnknownDb_Is404_AndNothingIsCreated()
    {
        var c = _f.NewApiKeyClient();
        var r = await c.GetAsync("/api/dbs/Test_absent/stats");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_f.Root, "Test_absent")));   // R1: no silent empty DB
    }

    [Fact]
    public async Task Sandbox_NonRocksDbDirectory_Is404_AndNotOpened()
    {
        var dir = Path.Combine(_f.Root, "plaindir");
        Directory.CreateDirectory(dir);
        var r = await _f.NewApiKeyClient().GetAsync("/api/dbs/plaindir/stats");
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(Directory.GetFileSystemEntries(dir));
    }

    // ---- read endpoints ----

    [Fact]
    public async Task ListDbs_ShowsSeededDb_NotKeyDirOrPlainDirs()
    {
        var j = await Json(await _f.NewApiKeyClient().GetAsync("/api/dbs"));
        var names = j.GetProperty("databases").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(CrackDbWebFactory.DbName, names);
        Assert.DoesNotContain("keys-not-a-db", names);
        Assert.DoesNotContain("plaindir", names);
    }

    [Fact]
    public async Task Stats_EstimatedAndFull()
    {
        var c = _f.NewApiKeyClient();
        var j = await Json(await c.GetAsync("/api/dbs/Test_words/stats"));
        Assert.False(j.GetProperty("full").GetBoolean());
        Assert.True(j.GetProperty("estimatedCount").GetInt64() >= 0);
        Assert.Equal(JsonValueKind.Null, j.GetProperty("lengths").ValueKind);

        var full = await Json(await c.GetAsync("/api/dbs/Test_words/stats?full=true"));
        var lengths = full.GetProperty("lengths").EnumerateArray()
            .ToDictionary(e => e.GetProperty("length").GetInt32(), e => e.GetProperty("count").GetInt64());
        Assert.Equal(1, lengths[1]); Assert.Equal(2, lengths[2]); Assert.Equal(1, lengths[3]);
        Assert.Equal(1, lengths[4]); Assert.Equal(1, lengths[6]); Assert.Equal(1, lengths[7]);
        Assert.Equal(1, lengths[8]); Assert.Equal(1, lengths[10]);
        Assert.Equal(CrackDbWebFactory.Words.Length, lengths.Values.Sum());
    }

    [Fact]
    public async Task Count_ExactForRange_LengthShorthand_AndEstimatedWithoutBounds()
    {
        var c = _f.NewApiKeyClient();
        var range = await Json(await c.GetAsync("/api/dbs/Test_words/count?min=3&max=4"));
        Assert.True(range.GetProperty("exact").GetBoolean());
        Assert.Equal(2, range.GetProperty("count").GetInt64());   // abc, abcd

        var one = await Json(await c.GetAsync("/api/dbs/Test_words/count?length=2"));
        Assert.Equal(2, one.GetProperty("min").GetInt32());
        Assert.Equal(2, one.GetProperty("max").GetInt32());
        Assert.Equal(2, one.GetProperty("count").GetInt64());

        var all = await Json(await c.GetAsync("/api/dbs/Test_words/count"));
        Assert.False(all.GetProperty("exact").GetBoolean());
    }

    [Theory]
    [InlineData("count?min=-1")]
    [InlineData("count?min=5&max=2")]
    [InlineData("count?min=abc")]
    [InlineData("lookup")]
    [InlineData("lookup?word=")]
    public async Task BadParameters_Are400(string path)
    {
        var r = await _f.NewApiKeyClient().GetAsync("/api/dbs/Test_words/" + path);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("hunter2", true)]
    [InlineData("Hunter2", false)]
    [InlineData("nothere", false)]
    public async Task Lookup_FoundAndNotFound(string word, bool found)
    {
        var r = await _f.NewApiKeyClient().GetAsync($"/api/dbs/Test_words/lookup?word={Uri.EscapeDataString(word)}");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(found, (await Json(r)).GetProperty("found").GetBoolean());
    }

    // ---- streamed export ----

    [Fact]
    public async Task Export_Json_MatchesRangeOrder_WithHeaders()
    {
        var c = _f.NewApiKeyClient();
        var r = await c.PostAsJsonAsync("/api/dbs/Test_words/export", new { min = 2, max = 7 });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("attachment; filename=\"Test_words_2-7_", r.Content.Headers.ContentDisposition?.ToString() ?? "");
        Assert.StartsWith("text/plain", r.Content.Headers.ContentType?.ToString());
        var body = await r.Content.ReadAsStringAsync();
        Assert.Equal(string.Join("\n", Expected(2, 7, null)) + "\n", body);
    }

    [Fact]
    public async Task Export_FormPost_WithCookie_LengthLimitAndAll()
    {
        var c = await _f.NewLoggedInClientAsync();
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["min"] = "", ["max"] = "", ["limit"] = "4" });
        var limited = await c.PostAsync("/api/dbs/Test_words/export", form);
        Assert.Equal(string.Join("\n", Expected(null, null, 4)) + "\n", await limited.Content.ReadAsStringAsync());

        var all = await c.PostAsync("/api/dbs/Test_words/export", null);
        Assert.Equal(string.Join("\n", Expected(null, null, null)) + "\n", await all.Content.ReadAsStringAsync());

        var len = await c.PostAsJsonAsync("/api/dbs/Test_words/export", new { length = 2 });
        Assert.Equal("ab\nzz\n", await len.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_BadParams_AndUnknownDb()
    {
        var c = _f.NewApiKeyClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/dbs/Test_words/export", new { min = 9, max = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/dbs/..%2fx/export", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/api/dbs/Test_nope/export", new { })).StatusCode);
    }

    [Fact]
    public async Task Export_ReleasesLease_SoWriteWindowCanRun()
    {
        var c = _f.NewApiKeyClient();
        await (await c.PostAsJsonAsync("/api/dbs/Test_words/export", new { })).Content.ReadAsStringAsync();
        var mgr = _f.Services.GetRequiredService<IDbManager>();
        var got = await mgr.WithWriteAsync("Test_words", DbOpenMode.Read,
            (db, _) => Task.FromResult(db.Contains("hunter2")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(got);
    }

        // Runs the real app as a child process instead of through WebApplicationFactory. With a failing
    // ValidateOnStart the factory's deferred host races the entry point's host disposal and can surface an
    // ObjectDisposedException('IServiceProvider') instead of the validation error (framework behaviour,
    // not an app bug), so the in-process variant was flaky. The process exit is deterministic.
    [Fact]
    public async Task Startup_FailsFast_WithoutCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        CrackDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        try
        {
            var dll = typeof(Program).Assembly.Location;
            var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add(dll);
            foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("CRACKDB_", StringComparison.OrdinalIgnoreCase)).ToList())
                psi.Environment.Remove(k);
            psi.Environment["CRACKDB_Storage__DataRoot"] = root;
            psi.Environment["CRACKDB_Storage__KeyPath"] = Path.Combine(root, "keys");
            psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";

            using var proc = System.Diagnostics.Process.Start(psi)!;
            var output = Task.WhenAll(proc.StandardOutput.ReadToEndAsync(), proc.StandardError.ReadToEndAsync());
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await proc.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { proc.Kill(entireProcessTree: true); throw; }
            var text = string.Join("\n", await output);

            Assert.NotEqual(0, proc.ExitCode);
            Assert.Contains("OptionsValidationException", text);
            Assert.Contains("Auth:PasswordHash is required", text);
        }
        finally { Directory.Delete(root, true); }
    }
}
