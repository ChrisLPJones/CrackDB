using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CrackDB.Web.Tests;

public class UploadTests
{
    private static MultipartFormDataContent Form(string db, byte[] file, string fileName = "my list.txt", string? min = null, string? max = null)
    {
        var form = new MultipartFormDataContent();
        form.Add(new StringContent(db), "db");
        if (min != null) form.Add(new StringContent(min), "min");
        if (max != null) form.Add(new StringContent(max), "max");
        var fc = new ByteArrayContent(file);
        fc.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(fc, "file", fileName);
        return form;
    }

    private static string[] StagedFiles(CrackDbWebFactory f)
    {
        var d = Path.Combine(f.Incoming, ".uploads");
        return Directory.Exists(d) ? Directory.GetFiles(d) : [];
    }

    private static async Task<JsonElement> Wait(HttpClient c, string id)
    {
        for (int i = 0; i < 2400; i++)
        {
            var j = JsonDocument.Parse(await c.GetStringAsync("/api/jobs/" + id)).RootElement.Clone();
            if (j.GetProperty("status").GetString() is "completed" or "failed" or "cancelled") return j;
            await Task.Delay(25);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task SmallUpload_Imports_AndStagedFileIsDeleted()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var data = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 500).Select(i => "up" + i)) + "\n");
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_up", data));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var j = await Wait(c, id);
        Assert.Equal("completed", j.GetProperty("status").GetString());
        Assert.Equal(500, j.GetProperty("linesRead").GetInt64());
        Assert.Equal("upload:my list.txt", j.GetProperty("source").GetString());
        Assert.DoesNotContain(f.Incoming, j.GetRawText());
        Assert.Empty(StagedFiles(f));

        var lk = JsonDocument.Parse(await c.GetStringAsync("/api/dbs/Test_up/lookup?word=up123")).RootElement;
        Assert.True(lk.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task OverCapUpload_Is413_AndNothingIsLeftBehind()
    {
        using var f = new CrackDbWebFactory(1000, new() { ["Upload:MaxBytes"] = "1000" });
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_big", new byte[5000]));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, r.StatusCode);
        Assert.Empty(StagedFiles(f));
        Assert.DoesNotContain("Test_big", JsonDocument.Parse(await c.GetStringAsync("/api/dbs")).RootElement
            .GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Upload_BadDbName_Is400_AndLeavesNothing()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PostAsync("/api/jobs/upload", Form("../evil", new byte[] { 65, 10 }));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(StagedFiles(f));
    }

    [Fact]
    public async Task Upload_FileBeforeDb_Is400()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(new byte[] { 65, 10 }), "file", "x.txt");
        form.Add(new StringContent("Test_x"), "db");
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/jobs/upload", form)).StatusCode);
        Assert.Empty(StagedFiles(f));
    }

    [Fact]
    public async Task Upload_NotMultipart_Is400()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var r = await c.PostAsync("/api/jobs/upload", new StringContent("x", Encoding.UTF8, "text/plain"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Startup_RemovesOrphanedStagedUploads()
    {
        var f = new CrackDbWebFactory();
        try
        {
            var up = Path.Combine(f.Incoming, ".uploads");
            Directory.CreateDirectory(up);
            File.WriteAllText(Path.Combine(up, "orphan.part"), "x");
            var c = await f.NewLoggedInClientAsync();   // starts the host, whose JobQueue cleans the dir
            Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/api/jobs")).StatusCode);
            Assert.Empty(StagedFiles(f));
        }
        finally { f.Dispose(); }
    }
}
