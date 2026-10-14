using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CrackDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CrackDB.Web.Tests;

public class DeleteDbTests : IClassFixture<CrackDbWebFactory>
{
    private readonly CrackDbWebFactory _f;
    public DeleteDbTests(CrackDbWebFactory f) => _f = f;

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private async Task Create(HttpClient c, string name) =>
        Assert.Equal(HttpStatusCode.Created, (await c.PostAsJsonAsync("/api/dbs", new { name })).StatusCode);

    private static async Task<List<string?>> Names(HttpClient c) =>
        (await Json(await c.GetAsync("/api/dbs"))).GetProperty("databases").EnumerateArray().Select(x => x.GetString()).ToList();

    [Fact]
    public async Task Delete_Returns204_RemovesDir_AndReadsThen404()
    {
        var c = _f.NewApiKeyClient();
        await Create(c, "Test_del");
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs/Test_del/stats")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/dbs/Test_del")).StatusCode);
        Assert.DoesNotContain("Test_del", await Names(c));
        Assert.False(Directory.Exists(Path.Combine(_f.Root, "Test_del")));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dbs/Test_del/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/dbs/Test_del/lookup?word=x")).StatusCode);
    }

    [Fact]
    public async Task Delete_MissingAndDoubleDelete_Are404()
    {
        var c = _f.NewApiKeyClient();
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/dbs/Test_nope")).StatusCode);
        await Create(c, "Test_twice");
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/dbs/Test_twice")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync("/api/dbs/Test_twice")).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_f.Root, "Test_twice")));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("%2e%2e")]
    [InlineData("..%2fx")]
    [InlineData("%2fetc%2fx")]
    [InlineData("a%2fb")]
    [InlineData("a%5cb")]
    [InlineData("bad%20name")]
    [InlineData("a:b")]
    [InlineData("-leading")]
    [InlineData(".hidden")]
    public async Task Delete_InvalidOrTraversal_IsRejected_AndNothingElseRemoved(string name)
    {
        // Sentinel siblings: a dir next to the root and a db inside it.
        var sentinel = Path.Combine(Path.GetDirectoryName(_f.Root)!, "Test_sentinel_" + Guid.NewGuid());
        Directory.CreateDirectory(sentinel);
        File.WriteAllText(Path.Combine(sentinel, "CURRENT"), "x");
        var c = _f.NewApiKeyClient();
        try
        {
            var r = await c.DeleteAsync("/api/dbs/" + name);
            // 400 from the resolver; a URL that the router itself rejects/normalises may be 404/405 instead - never 204.
            Assert.NotEqual(HttpStatusCode.NoContent, r.StatusCode);
            Assert.True(r.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed, r.StatusCode.ToString());
            Assert.True(File.Exists(Path.Combine(sentinel, "CURRENT")));
            Assert.True(Directory.Exists(_f.Root));
            Assert.Contains(CrackDbWebFactory.DbName, await Names(c));
        }
        finally { Directory.Delete(sentinel, true); }
    }

    [Theory]
    [InlineData("..%2fx")]
    [InlineData("%2fetc%2fx")]
    [InlineData("a%5cb")]
    [InlineData("bad%20name")]
    [InlineData("a:b")]
    [InlineData("-leading")]
    [InlineData(".hidden")]
    public async Task Delete_InvalidName_Is400(string name) =>
        Assert.Equal(HttpStatusCode.BadRequest, (await _f.NewApiKeyClient().DeleteAsync("/api/dbs/" + name)).StatusCode);

    [Fact]
    public async Task Delete_Unauthenticated_Is401_AndNothingDeleted()
    {
        var c = _f.NewApiKeyClient();
        await Create(c, "Test_anon_del");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _f.NewClient().DeleteAsync("/api/dbs/Test_anon_del")).StatusCode);
        Assert.True(File.Exists(Path.Combine(_f.Root, "Test_anon_del", "CURRENT")));
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs/Test_anon_del/stats")).StatusCode);
    }

    [Fact]
    public async Task Delete_LeavesOtherDatabasesIntact()
    {
        var c = _f.NewApiKeyClient();
        await Create(c, "Test_iso_a");
        await Create(c, "Test_iso_b");
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync("/api/dbs/Test_iso_a")).StatusCode);
        var names = await Names(c);
        Assert.DoesNotContain("Test_iso_a", names);
        Assert.Contains("Test_iso_b", names);
        Assert.Contains(CrackDbWebFactory.DbName, names);
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/dbs/Test_iso_b/stats")).StatusCode);
        var look = await Json(await c.GetAsync($"/api/dbs/{CrackDbWebFactory.DbName}/lookup?word=abc"));
        Assert.True(look.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task Delete_WhileImportRunning_Is409_AndDbSurvives()
    {
        var hook = new BlockingHook();
        using var f = new CrackDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        var sb = new StringBuilder();
        for (int i = 0; i < 5000; i++) sb.Append('w').Append(i.ToString("D8")).Append('\n');
        File.WriteAllText(Path.Combine(f.Incoming, "a.txt"), sb.ToString());
        File.WriteAllText(Path.Combine(f.Incoming, "b.txt"), sb.ToString());

        hook.Arm();
        try
        {
            var a = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_busy", source = "a.txt" });
            Assert.Equal(HttpStatusCode.Accepted, a.StatusCode);
            Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));          // running, holds the WriteGate
            Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync("/api/dbs/Test_busy")).StatusCode);

            // Queued-only: another db's job runs first (global serial queue), so this one is merely queued.
            var q = await c.PostAsJsonAsync("/api/jobs/import", new { db = CrackDbWebFactory.DbName, source = "b.txt" });
            Assert.Equal(HttpStatusCode.Accepted, q.StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await c.DeleteAsync("/api/dbs/" + CrackDbWebFactory.DbName)).StatusCode);
        }
        finally { hook.Disarm(); }

        // Wait for the jobs to finish, then the db still exists.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            var jobs = (await Json(await c.GetAsync("/api/jobs"))).GetProperty("jobs");
            if (jobs.EnumerateArray().All(j => j.GetProperty("status").GetString() is "completed" or "failed" or "cancelled")) break;
            await Task.Delay(50);
        }
        Assert.True(Directory.Exists(Path.Combine(f.Root, "Test_busy")));
        Assert.Contains(CrackDbWebFactory.DbName, await Names(c));
    }

    [Fact]
    public async Task Manager_CreateDeleteCreate_SameName_StartsEmpty_AndFullLifecycle()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        CrackDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        var mgr = new DbManager(new DataRootResolver(root));
        try
        {
            await mgr.CreateAsync("Test_x", CancellationToken.None);
            await mgr.WithWriteAsync("Test_x", DbOpenMode.Bulk, (db, _) =>
            {
                db.Import(new[] { "hello", "world" }, null, null);
                return Task.FromResult(true);
            }, CancellationToken.None);
            using (var l = mgr.AcquireRead("Test_x")) Assert.True(l.Db.Contains("hello"));

            await mgr.DeleteAsync("Test_x", CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(root, "Test_x")));
            Assert.Throws<DbNotFoundException>(() => mgr.AcquireRead("Test_x"));
            await Assert.ThrowsAsync<DbNotFoundException>(() => mgr.DeleteAsync("Test_x", CancellationToken.None));

            await mgr.CreateAsync("Test_x", CancellationToken.None);
            using var lease = mgr.AcquireRead("Test_x");
            Assert.Equal(0, lease.Db.EstimatedCount());
            Assert.False(lease.Db.Contains("hello"));
        }
        finally
        {
            await mgr.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Manager_ReaderParkedDuringDelete_Gets404_AndDirIsNotRecreated()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        CrackDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        var mgr = new DbManager(new DataRootResolver(root));
        try
        {
            await mgr.CreateAsync("Test_race", CancellationToken.None);
            var held = mgr.AcquireRead("Test_race");                 // keeps Readers > 0: delete stays in its drain window
            var del = mgr.DeleteAsync("Test_race", CancellationToken.None);

            var entries = (System.Collections.IDictionary)typeof(DbManager)
                .GetField("_entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(mgr)!;
            var entry = entries["Test_race"]!;
            var swapping = entry.GetType().GetField("Swapping")!;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (!(bool)swapping.GetValue(entry)! && DateTime.UtcNow < deadline) await Task.Delay(10);
            Assert.True((bool)swapping.GetValue(entry)!);

            // Passes ResolveExisting (dir still exists), then parks on Swapping.
            var reader = Task.Run(() => mgr.AcquireRead("Test_race"));
            await Task.Delay(200);
            Assert.False(reader.IsCompleted);
            // A second delete cannot take the gate: fail-fast 409.
            await Assert.ThrowsAsync<DbBusyException>(() => mgr.DeleteAsync("Test_race", CancellationToken.None));

            held.Dispose();
            await del;
            await Assert.ThrowsAsync<DbNotFoundException>(() => reader);
            Assert.False(Directory.Exists(Path.Combine(root, "Test_race")));
            Assert.Empty(mgr.ListDatabases());
        }
        finally
        {
            await mgr.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task Manager_ReadersRacingDelete_NeverResurrectTheDirectory()
    {
        // Reproduces the fresh-entry race: a reader passes ResolveExisting, the delete finishes and removes the
        // entry, then the reader GetOrAdds a NEW entry and (without the IsDatabaseDir re-check) the always-create
        // engine ctor re-creates the deleted dir. Stress the interleaving over many rounds.
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        CrackDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        var mgr = new DbManager(new DataRootResolver(root));
        try
        {
            for (int round = 0; round < 60; round++)
            {
                await mgr.CreateAsync("Test_res", CancellationToken.None);
                using var stop = new CancellationTokenSource();
                var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        try { mgr.AcquireRead("Test_res").Dispose(); }
                        catch (DbNotFoundException) { }
                    }
                })).ToArray();
                await Task.Delay(round % 5);
                await mgr.DeleteAsync("Test_res", CancellationToken.None);
                await Task.Delay(20);
                stop.Cancel();
                await Task.WhenAll(readers);
                Assert.False(Directory.Exists(Path.Combine(root, "Test_res")), "deleted dir was recreated in round " + round);
                Assert.Throws<DbNotFoundException>(() => mgr.AcquireRead("Test_res"));
            }
        }
        finally
        {
            await mgr.DisposeAsync();
            Directory.Delete(root, true);
        }
    }
}
