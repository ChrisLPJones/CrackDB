using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CrackDB.Web.Jobs;

namespace CrackDB.Web.Tests;

/// <summary>Blocks a running search right after a chunk's lease is released, until released.</summary>
public sealed class BlockingSearchHook : ISearchProgressHook
{
    private volatile bool _armed;
    public ManualResetEventSlim Reached { get; private set; } = new(false);
    public ManualResetEventSlim Release { get; private set; } = new(false);

    public void Arm() { Reached = new(false); Release = new(false); _armed = true; }
    public void Disarm() { _armed = false; Release.Set(); }

    public void OnChunk(string dbName, long scannedCount)
    {
        if (!_armed) return;
        var (reached, release) = (Reached, Release);
        reached.Set();
        release.Wait(TimeSpan.FromSeconds(60));
    }
}

public class SearchTests
{
    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static Task<HttpResponseMessage> Search(HttpClient c, string db, string query, int? min = null, int? max = null, int? cap = null) =>
        c.PostAsJsonAsync($"/api/dbs/{db}/search", new { query, min, max, cap });

    private static async Task<JsonElement> Get(HttpClient c, string id) =>
        await Body(await c.GetAsync("/api/jobs/" + id));

    private static async Task<JsonElement> WaitStatus(HttpClient c, string id, params string[] statuses)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var j = await Get(c, id);
            if (statuses.Contains(j.GetProperty("status").GetString())) return j;
            await Task.Delay(25);
        }
        throw new TimeoutException($"job {id} did not reach {string.Join('|', statuses)}");
    }

    private static string[] PasswordWords => new[]
    {
        "password1", "password123", "xpassword1", "PASSWORD1", "nomatch", "short", "pw",
    };

    // ---- exact match: answered inline, before the background job even starts scanning ----

    [Fact]
    public async Task ExactMatch_AnsweredImmediately()
    {
        using var f = new CrackDbWebFactory();
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_exact"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var r = await Search(c, "Test_exact", "password1");
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var body = await Body(r);
        Assert.True(body.GetProperty("exactFound").GetBoolean());
        var id = body.GetProperty("id").GetString()!;
        await WaitStatus(c, id, "completed");   // let the job finish so the single-search gate is free for the next test
    }

    [Fact]
    public async Task ExactMatch_FalseWhenOnlyASubstringMatches()
    {
        using var f = new CrackDbWebFactory();
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_exact2"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var r = await Search(c, "Test_exact2", "assword1");   // substring of password1/xpassword1, not stored itself
        var body = await Body(r);
        Assert.False(body.GetProperty("exactFound").GetBoolean());
        var j = await WaitStatus(c, body.GetProperty("id").GetString()!, "completed");
        var matches = j.GetProperty("matches").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        // Case-insensitive, so PASSWORD1 also matches "assword1" (it equals "password1" case-folded).
        Assert.Equal(
            new[] { "PASSWORD1", "password1", "password123", "xpassword1" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            matches);
    }

    [Fact]
    public async Task SubstringSearch_IsCaseInsensitive_StartMiddleEnd()
    {
        using var f = new CrackDbWebFactory();
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_ci"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var r = await Search(c, "Test_ci", "password1");
        var id = (await Body(r)).GetProperty("id").GetString()!;
        var j = await WaitStatus(c, id, "completed");
        var matches = j.GetProperty("matches").EnumerateArray().Select(x => x.GetString()).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            new[] { "PASSWORD1", "password1", "password123", "xpassword1" }.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            matches);
        Assert.DoesNotContain("nomatch", matches);
    }

    [Fact]
    public async Task SubstringSearch_Negative_FindsNothing()
    {
        using var f = new CrackDbWebFactory();
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_neg"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        var r = await Search(c, "Test_neg", "zzzznotfound");
        var j = await WaitStatus(c, (await Body(r)).GetProperty("id").GetString()!, "completed");
        Assert.Empty(j.GetProperty("matches").EnumerateArray());
        Assert.Equal(0, j.GetProperty("matchedCount").GetInt64());
    }

    // ---- minimum query length: server-side, not just the UI ----

    [Theory]
    [InlineData("abc")]      // 3 chars, below the 4-char minimum
    [InlineData("")]
    public async Task TooShortQuery_Is400(string query)
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await Search(c, CrackDbWebFactory.DbName, query);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ExactlyMinimumLength_IsAccepted()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await Search(c, CrackDbWebFactory.DbName, "abcd");   // exactly 4 chars = SearchRunner.MinQueryLength
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        await WaitStatus(c, (await Body(r)).GetProperty("id").GetString()!, "completed");
    }

    // ---- result cap enforced server-side ----

    [Fact]
    public async Task ResultCap_StopsAtTheRequestedCap_AndMarksCapped()
    {
        using var f = new CrackDbWebFactory();
        var words = Enumerable.Range(0, 20).Select(i => "needle" + i.ToString("D4")).ToArray();   // 20 matches
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_cap"), words);
        var c = await f.NewLoggedInClientAsync();

        var r = await Search(c, "Test_cap", "needle", cap: 5);
        var j = await WaitStatus(c, (await Body(r)).GetProperty("id").GetString()!, "completed");
        Assert.Equal(5, j.GetProperty("matches").GetArrayLength());
        Assert.True(j.GetProperty("capped").GetBoolean());
        Assert.Equal(5, j.GetProperty("matchedCount").GetInt64());
    }

    [Fact]
    public async Task ResultCap_RequestAboveHardMax_IsClampedNotHonoured()
    {
        using var f = new CrackDbWebFactory();
        var words = Enumerable.Range(0, 5).Select(i => "needle" + i).ToArray();
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_capmax"), words);
        var c = await f.NewLoggedInClientAsync();

        var r = await Search(c, "Test_capmax", "needle", cap: 999_999);
        var body = await Body(r);
        Assert.Equal(SearchRunner.MaxResultCap, body.GetProperty("cap").GetInt32());
        await WaitStatus(c, body.GetProperty("id").GetString()!, "completed");   // free the single-search gate for later tests
    }

    // ---- one search at a time, globally ----

    [Fact]
    public async Task SecondConcurrentSearch_Is409_ThenSucceedsAfterTheFirstFinishes()
    {
        var hook = new BlockingSearchHook();
        using var f = new CrackDbWebFactory(1000, null, null, null, hook);
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_s1"), PasswordWords);
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_s2"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        hook.Arm();
        var first = await Body(await Search(c, "Test_s1", "password1"));
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));   // the first search is paused mid-scan

        var secondResp = await Search(c, "Test_s2", "password1");   // a different db: still refused - the gate is global
        Assert.Equal(HttpStatusCode.Conflict, secondResp.StatusCode);

        hook.Disarm();
        await WaitStatus(c, first.GetProperty("id").GetString()!, "completed");

        var third = await Search(c, "Test_s2", "password1");   // the gate is free again
        Assert.Equal(HttpStatusCode.Accepted, third.StatusCode);
        await WaitStatus(c, (await Body(third)).GetProperty("id").GetString()!, "completed");
    }

    // ---- cancel ----

    [Fact]
    public async Task Cancel_StopsASearchMidScan()
    {
        var hook = new BlockingSearchHook();
        using var f = new CrackDbWebFactory(1000, null, null, null, hook);
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_cancel"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        hook.Arm();
        var id = (await Body(await Search(c, "Test_cancel", "password1"))).GetProperty("id").GetString()!;
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        var cancelResp = await c.PostAsync($"/api/jobs/{id}/cancel", null);
        Assert.Equal(HttpStatusCode.Accepted, cancelResp.StatusCode);
        hook.Disarm();

        var j = await WaitStatus(c, id, "cancelled", "completed", "failed");
        Assert.Equal("cancelled", j.GetProperty("status").GetString());
    }

    // ---- concurrency: a paused search on db A doesn't starve a same-db import beyond ~one chunk,
    // and never blocks a job on a different db at all ----

    [Fact]
    public async Task PausedSearch_ReleasesItsLeaseBetweenChunks_SameDbImportProceedsPromptly()
    {
        var hook = new BlockingSearchHook();
        using var f = new CrackDbWebFactory(1000, null, null, null, hook);
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_conc"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();
        File.WriteAllText(Path.Combine(f.Incoming, "more.txt"), "freshword1\nfreshword2\n");

        hook.Arm();
        var searchId = (await Body(await Search(c, "Test_conc", "password1"))).GetProperty("id").GetString()!;
        // Paused right after releasing its lease for the (only) chunk: the db is NOT held open.
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        // An import into the SAME db must be able to claim the write window and finish while the
        // search job is still "running" (paused in the hook) - proving the search does not hold the
        // lease across the pause.
        var importResp = await c.PostAsJsonAsync("/api/jobs/import", new { db = "Test_conc", source = "more.txt" });
        Assert.Equal(HttpStatusCode.Accepted, importResp.StatusCode);
        var importId = (await Body(importResp)).GetProperty("id").GetString()!;
        var importJob = await WaitStatus(c, importId, "completed", "failed");
        Assert.Equal("completed", importJob.GetProperty("status").GetString());

        // The search job itself is still paused/running throughout - it was never torn down by the import.
        Assert.Equal("running", (await Get(c, searchId)).GetProperty("status").GetString());

        hook.Disarm();
        await WaitStatus(c, searchId, "completed");
    }

    [Fact]
    public async Task PausedSearch_NeverBlocksAJobOnADifferentDatabase()
    {
        var hook = new BlockingSearchHook();
        using var f = new CrackDbWebFactory(1000, null, null, null, hook);
        CrackDbWebFactory.Seed(Path.Combine(f.Root, "Test_a"), PasswordWords);
        var c = await f.NewLoggedInClientAsync();

        hook.Arm();
        var searchId = (await Body(await Search(c, "Test_a", "password1"))).GetProperty("id").GetString()!;
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));

        // A compact on an entirely different db (the seeded default) is unaffected by the paused search.
        var compactResp = await c.PostAsJsonAsync("/api/jobs/compact", new { db = CrackDbWebFactory.DbName });
        Assert.Equal(HttpStatusCode.Accepted, compactResp.StatusCode);
        var compactId = (await Body(compactResp)).GetProperty("id").GetString()!;
        await WaitStatus(c, compactId, "completed");

        hook.Disarm();
        await WaitStatus(c, searchId, "completed");
    }

    // ---- auth ----

    [Fact]
    public async Task SearchRoute_WithoutCredentials_Is401()
    {
        using var f = new CrackDbWebFactory();
        var r = await f.NewClient().PostAsJsonAsync($"/api/dbs/{CrackDbWebFactory.DbName}/search", new { query = "abcd" });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}
