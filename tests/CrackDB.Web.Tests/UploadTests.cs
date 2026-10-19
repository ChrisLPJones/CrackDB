using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using CrackDB.Web.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace CrackDB.Web.Tests;

// The upload gate is process-wide (static), so every class that uploads shares this collection and runs serially.
[Collection("Uploads")]
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
    public async Task UnlimitedUpload_MaxBytesZero_StreamsImports_AndDeletesStagedFile()
    {
        using var f = new CrackDbWebFactory(1000, new() { ["Upload:MaxBytes"] = "0" });
        var c = await f.NewLoggedInClientAsync();
        // ~3.4 MB: well past the 64 KiB global request limit and the 1000-byte cap used by the 413 test.
        var data = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 300_000).Select(i => "unl" + i)) + "\n");
        Assert.True(data.Length > 2_000_000);
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_unl", data));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;

        var j = await Wait(c, id);
        Assert.Equal("completed", j.GetProperty("status").GetString());
        Assert.Equal(300_000, j.GetProperty("linesRead").GetInt64());
        Assert.DoesNotContain(f.Incoming, j.GetRawText());
        Assert.Empty(StagedFiles(f));
        var lk = JsonDocument.Parse(await c.GetStringAsync("/api/dbs/Test_unl/lookup?word=unl299999")).RootElement;
        Assert.True(lk.GetProperty("found").GetBoolean());
    }

    [Fact]
    public async Task UnlimitedUpload_AbortsWith507_WhenDiskRunsLowMidStream_AndDeletesPartialFile()
    {
        using var baseF = new CrackDbWebFactory(1000, new() { ["Upload:MaxBytes"] = "0" });
        using var f = baseF.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddSingleton<IDiskSpace>(new FakeDisk())));
        var c = f.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = true });
        var login = await c.PostAsJsonAsync("/login", new { username = CrackDbWebFactory.Username, password = CrackDbWebFactory.Password });
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);

        var data = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(0, 50_000).Select(i => "lowdisk" + i)) + "\n");
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_low", data));
        Assert.Equal(HttpStatusCode.InsufficientStorage, r.StatusCode);
        var body = await r.Content.ReadAsStringAsync();
        Assert.DoesNotContain(baseF.Incoming, body);
        Assert.Empty(StagedFiles(baseF));
        Assert.DoesNotContain("Test_low", JsonDocument.Parse(await c.GetStringAsync("/api/dbs")).RootElement
            .GetProperty("databases").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public void DiskRecheckInterval_IsWithinTheReserve()
    {
        Assert.True(new DriveDiskSpace().RecheckIntervalBytes <= CrackDB.Web.Endpoints.JobEndpoints.DiskReserveBytes / 2);
    }

    [Fact]
    public async Task Upload_WhileAnotherIsInFlight_Is409_AndFirstStillCompletes()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new MultipartFormDataContent();
        first.Add(new StringContent("Test_first"), "db");
        first.Add(new StreamContent(new GatedStream(Encoding.UTF8.GetBytes("a1\na2\n"), started, release.Task)), "file", "a.txt");
        var firstTask = c.PostAsync("/api/jobs/upload", first);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));   // the server is now reading the first file
        for (int i = 0; i < 400 && StagedFiles(f).Length == 0; i++) await Task.Delay(25);   // server holds the gate once the .part exists
        Assert.NotEmpty(StagedFiles(f));
        var second = await c.PostAsync("/api/jobs/upload", Form("Test_second", new byte[] { 65, 10 }));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.DoesNotContain(f.Incoming, await second.Content.ReadAsStringAsync());

        release.SetResult();
        var r = await firstTask;
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        Assert.Equal("completed", (await Wait(c, id)).GetProperty("status").GetString());

        // the gate was released: a later upload is accepted again
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync("/api/jobs/upload", Form("Test_third", new byte[] { 65, 10 }))).StatusCode);
    }

    [Fact]
    public async Task Import_SkipsOverlongLines_CountsThem_AndImportsTheRest()
    {
        using var f = new CrackDbWebFactory();
        var c = await f.NewLoggedInClientAsync();
        var text = "before1\nbefore2\n" + new string('x', 20_000) + "\nafter1\nafter2\n";
        var r = await c.PostAsync("/api/jobs/upload", Form("Test_long", Encoding.UTF8.GetBytes(text)));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        var id = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()!;
        var j = await Wait(c, id);
        Assert.Equal("completed", j.GetProperty("status").GetString());
        Assert.Equal(1, j.GetProperty("errors").GetInt32());
        foreach (var w in new[] { "before1", "before2", "after1", "after2" })
            Assert.True(JsonDocument.Parse(await c.GetStringAsync("/api/dbs/Test_long/lookup?word=" + w)).RootElement.GetProperty("found").GetBoolean());
    }

    private sealed class GatedStream(byte[] data, TaskCompletionSource started, Task gate) : Stream
    {
        private int _pos;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos > 0) { started.TrySetResult(); await gate.WaitAsync(ct); }   // first chunk flows, then stall
            var n = Math.Min(buffer.Length, data.Length - _pos);
            if (_pos == 0) n = Math.Min(n, 3);
            data.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }
        public override int Read(byte[] b, int o, int c) => ReadAsync(b.AsMemory(o, c)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin r) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    // Plenty of space for the pre-flight check, then "almost none" on every in-loop recheck.
    private sealed class FakeDisk : IDiskSpace
    {
        private int _calls;
        public long RecheckIntervalBytes => 64 * 1024;
        public long? FreeBytes(string dir) => Interlocked.Increment(ref _calls) == 1 ? long.MaxValue : 1024;
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
