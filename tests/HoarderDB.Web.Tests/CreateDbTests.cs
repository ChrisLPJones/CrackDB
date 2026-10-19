using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Tests;

public class CreateDbTests : IClassFixture<HoarderDbWebFactory>
{
    private readonly HoarderDbWebFactory _f;
    public CreateDbTests(HoarderDbWebFactory f) => _f = f;

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Create_Returns201_ListsIt_AndIsImmediatelyReadable()
    {
        var c = _f.NewApiKeyClient();
        var r = await c.PostAsJsonAsync("/api/dbs", new { name = "Test_new" });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        Assert.Equal("Test_new", (await Json(r)).GetProperty("name").GetString());

        var list = await Json(await c.GetAsync("/api/dbs"));
        Assert.Contains("Test_new", list.GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
        Assert.True(File.Exists(Path.Combine(_f.Root, "Test_new", "CURRENT")));

        var stats = await c.GetAsync("/api/dbs/Test_new/stats");
        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(0, (await Json(stats)).GetProperty("estimatedCount").GetInt64());

        var look = await c.GetAsync("/api/dbs/Test_new/lookup?word=anything");
        Assert.Equal(HttpStatusCode.OK, look.StatusCode);
        Assert.False((await Json(look)).GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Create_Duplicate_Is409()
    {
        var c = _f.NewApiKeyClient();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/dbs", new { name = HoarderDbWebFactory.DbName })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_dup" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/api/dbs", new { name = "Test_dup" })).StatusCode);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("/etc/x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("bad name")]
    [InlineData("a:b")]
    [InlineData("-leading")]
    [InlineData(".hidden")]
    [InlineData("")]
    public async Task Create_InvalidName_Is400_AndCreatesNothing(string name)
    {
        var r = await _f.NewApiKeyClient().PostAsJsonAsync("/api/dbs", new { name });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        // Nothing was created beside the root either (traversal target "../x" would be a sibling).
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(_f.Root)!, "x", "CURRENT")));
        if (name is not ("" or ".." or "../x" or "/etc/x"))
            Assert.False(Directory.Exists(Path.Combine(_f.Root, name)));
    }

    [Fact]
    public async Task Create_MissingNameOrBody_Is400()
    {
        var c = _f.NewApiKeyClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/api/dbs", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PostAsync("/api/dbs", new StringContent("{bad", Encoding.UTF8, "application/json"))).StatusCode);
    }

    [Fact]
    public async Task Create_WithoutCredentials_Is401_AndCreatesNothing()
    {
        var r = await _f.NewClient().PostAsJsonAsync("/api/dbs", new { name = "Test_anon" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_f.Root, "Test_anon")));
    }

    [Fact]
    public async Task Manager_Create_EnforcesLimit_AndDuplicates()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        var mgr = new DbManager(new DataRootResolver(root), maxDatabases: 2);
        try
        {
            await mgr.CreateAsync("Test_a", CancellationToken.None);
            await mgr.CreateAsync("Test_b", CancellationToken.None);
            await Assert.ThrowsAsync<DbAlreadyExistsException>(() => mgr.CreateAsync("Test_a", CancellationToken.None));
            await Assert.ThrowsAsync<DbLimitReachedException>(() => mgr.CreateAsync("Test_c", CancellationToken.None));
            Assert.Equal(new[] { "Test_a", "Test_b" }, mgr.ListDatabases());
            using var lease = mgr.AcquireRead("Test_a");
            Assert.Equal(0, lease.Db.EstimatedCount());
        }
        finally
        {
            await mgr.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void DefaultLimit_Is512() => Assert.Equal(512, DbManager.DefaultMaxDatabases);
}
