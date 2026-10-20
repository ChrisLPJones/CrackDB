using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HoarderDB.Web.Tests;

// Shares the "Uploads" collection: it uploads, and the upload gate is process-wide.
[Collection("Uploads")]
public class ImportSeenTests
{
    private static byte[] Lines(string prefix, int n) =>
        Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, n).Select(i => prefix + i)) + "\n");

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static async Task<JsonElement> Body(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task Import(HttpClient c, string db, byte[] data, string name = "list.txt")
    {
        var form = new MultipartFormDataContent { { new StringContent(db), "db" } };
        var fc = new ByteArrayContent(data);
        fc.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fc, "file", name);
        var r = await c.PostAsync("/api/jobs/upload", form);
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = (await Body(r)).GetProperty("id").GetString()!;
        for (var i = 0; i < 2400; i++)
        {
            var st = (await Body(await c.GetAsync("/api/jobs/" + id))).GetProperty("status").GetString();
            if (st is "completed" or "failed" or "cancelled") return;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    private static Task<HttpResponseMessage> Seen(HttpClient c, string db, object body) =>
        c.PostAsJsonAsync($"/api/dbs/{db}/imports/seen", body);

    [Fact]
    public async Task SizeMiss_SizeHitHashMiss_SizeHitHashHit()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("seen", 300);
        await Import(c, "Test_s1", data);

        var miss = await Body(await Seen(c, "Test_s1", new { size = data.Length + 1 }));
        Assert.False(miss.GetProperty("sizeMatch").GetBoolean());
        Assert.False(miss.GetProperty("duplicate").GetBoolean());

        var sizeOnly = await Body(await Seen(c, "Test_s1", new { size = data.Length }));
        Assert.True(sizeOnly.GetProperty("sizeMatch").GetBoolean());
        Assert.False(sizeOnly.GetProperty("duplicate").GetBoolean());

        var other = await Body(await Seen(c, "Test_s1", new { size = data.Length, sha256 = new string('a', 64) }));
        Assert.True(other.GetProperty("sizeMatch").GetBoolean());
        Assert.False(other.GetProperty("duplicate").GetBoolean());

        var hit = await Body(await Seen(c, "Test_s1", new { size = data.Length, sha256 = Sha(data).ToUpperInvariant() }));
        Assert.True(hit.GetProperty("sizeMatch").GetBoolean());
        Assert.True(hit.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task HashHitWithWrongSize_IsNotADuplicate_AndOtherDbDoesNotSeeIt()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("scope", 200);
        await Import(c, "Test_s2", data);
        await Import(c, "Test_s3", Lines("elsewhere", 50));

        var wrongSize = await Body(await Seen(c, "Test_s2", new { size = data.Length + 7, sha256 = Sha(data) }));
        Assert.False(wrongSize.GetProperty("duplicate").GetBoolean());
        var otherDb = await Body(await Seen(c, "Test_s3", new { size = data.Length, sha256 = Sha(data) }));
        Assert.False(otherDb.GetProperty("sizeMatch").GetBoolean());
        Assert.False(otherDb.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task FailedImport_DoesNotCount()
    {
        var hook = new ThrowingHook { Armed = true };
        using var f = new HoarderDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("fail", 3000);
        await Import(c, "Test_s4", data);
        var r = await Body(await Seen(c, "Test_s4", new { size = data.Length, sha256 = Sha(data) }));
        Assert.False(r.GetProperty("sizeMatch").GetBoolean());
        Assert.False(r.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task CancelledImport_DoesNotCount()
    {
        var hook = new BlockingHook();
        using var f = new HoarderDbWebFactory(1000, null, null, hook);
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("can", 5000);
        hook.Arm();
        var form = new MultipartFormDataContent { { new StringContent("Test_s5"), "db" } };
        form.Add(new ByteArrayContent(data), "file", "x.txt");
        var up = await c.PostAsync("/api/jobs/upload", form);
        Assert.Equal(HttpStatusCode.Accepted, up.StatusCode);
        var id = (await Body(up)).GetProperty("id").GetString()!;
        Assert.True(hook.Reached.Wait(TimeSpan.FromSeconds(30)));
        await c.PostAsync($"/api/jobs/{id}/cancel", null);
        hook.Disarm();
        for (var i = 0; i < 2400; i++)
        {
            if ((await Body(await c.GetAsync("/api/jobs/" + id))).GetProperty("status").GetString() == "cancelled") break;
            await Task.Delay(25);
        }
        var r = await Body(await Seen(c, "Test_s5", new { size = data.Length, sha256 = Sha(data) }));
        Assert.False(r.GetProperty("sizeMatch").GetBoolean());
        Assert.False(r.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task BackfilledImport_Counts()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        await Import(c, "Test_s6", Lines("base", 10));
        var hash = new string('c', 64);
        var bf = await c.PostAsJsonAsync("/api/dbs/Test_s6/imports/backfill",
            new[] { new { name = "old.txt", sha256 = hash, sizeBytes = 123456L, importedAtUtc = DateTimeOffset.UtcNow } });
        Assert.True(bf.IsSuccessStatusCode);
        var r = await Body(await Seen(c, "Test_s6", new { size = 123456L, sha256 = hash }));
        Assert.True(r.GetProperty("duplicate").GetBoolean());
    }

    [Fact]
    public async Task InvalidInputs_Are400_UnknownDb404()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        await Import(c, "Test_s7", Lines("v", 10));
        Assert.Equal(HttpStatusCode.BadRequest, (await Seen(c, "Test_s7", new { size = -1 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Seen(c, "Test_s7", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Seen(c, "Test_s7", new { size = 5, sha256 = "xyz" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Seen(c, "Test_s7", new { size = 5, sha256 = new string('g', 64) })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Seen(c, "Test_s7", new { size = 5, sha256 = new string('a', 65) })).StatusCode);
        var s = await c.PostAsync("/api/dbs/Test_s7/imports/seen", new StringContent("{\"size\":\"abc\"}", Encoding.UTF8, "application/json"));
        Assert.True((int)s.StatusCode is 400 or 415);
        Assert.Equal(HttpStatusCode.NotFound, (await Seen(c, "Test_nope", new { size = 5 })).StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_Is401()
    {
        using var f = new HoarderDbWebFactory();
        var anon = f.NewClient();
        var r = await Seen(anon, "Test_s8", new { size = 5 });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Response_HasOnlyTwoBooleans_NoHistoryLeak()
    {
        using var f = new HoarderDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Lines("leak", 100);
        await Import(c, "Test_s9", data, "secret-name.txt");
        var text = await (await Seen(c, "Test_s9", new { size = data.Length, sha256 = Sha(data) })).Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(text).RootElement;
        Assert.Equal(new[] { "duplicate", "sizeMatch" }, root.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.All(root.EnumerateObject(), p => Assert.True(p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False));
        Assert.DoesNotContain("secret-name", text);
        Assert.DoesNotContain(Sha(data), text);
    }
}
