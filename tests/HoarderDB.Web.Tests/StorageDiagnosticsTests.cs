using HoarderDB;
using HoarderDB.Web.Options;
using HoarderDB.Web.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace HoarderDB.Web.Tests;

public class StorageDiagnosticsTests
{
    private sealed class CaptureLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Text)> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { lock (Lines) Lines.Add((logLevel, formatter(state, exception))); }
    }

    private sealed class FakeReader(Func<string, string?> f) : ITextFileReader
    {
        public string? TryReadAllText(string path) => f(path);
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
        HoarderDbWebFactory.EnsureThrowaway(root);
        Directory.CreateDirectory(root);
        return root;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    [InlineData(-1, false)]
    public void MaxSubcompactions_Bounds(int n, bool ok)
    {
        var dir = Path.GetTempPath();
        var o = new StorageOptions { DataRoot = dir, KeyPath = Path.Combine(dir, "keys"), MaxSubcompactions = n };
        Assert.Equal(ok, new StorageOptionsValidator().Validate(null, o).Succeeded);
    }

    [Fact]
    public async Task StartAsync_LogsMemoryLine_NumbersOnly()
    {
        var root = NewRoot();
        try
        {
            var log = new CaptureLogger<StorageDiagnostics>();
            var reader = new FakeReader(p => p == MemoryDiagnostics.CgroupV2MemoryMax ? "8589934592" : p == MemoryDiagnostics.CgroupV2MemoryHigh ? "max" : null);
            var mgr = new DbManager(new DataRootResolver(root), storage: Microsoft.Extensions.Options.Options.Create(new StorageOptions { MaxSubcompactions = 2, ScanThreads = 3 }),
                diagnostics: new StorageDiagnostics(log, reader));
            await mgr.StartAsync(CancellationToken.None);
            var line = Assert.Single(log.Lines).Text;
            Assert.Contains("auto-compaction off", line);
            Assert.Contains("cgroup memory.max 8192 MB", line);
            Assert.Contains("memory.high n/a", line);
            Assert.Contains("max subcompactions 2", line);
            Assert.Contains("scan threads 3", line);
            Assert.DoesNotContain(root, line);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Diagnostics_NeverThrow_WhenProcAndCgroupUnreadable()
    {
        var root = NewRoot();
        try
        {
            var log = new CaptureLogger<StorageDiagnostics>();
            var diag = new StorageDiagnostics(log, new FakeReader(_ => throw new UnauthorizedAccessException()));
            var mgr = new DbManager(new DataRootResolver(root), diagnostics: diag);
            await mgr.StartAsync(CancellationToken.None);
            await mgr.CreateAsync("Test_diag", CancellationToken.None);   // open + job-start + job-finish + reopen all log
            using (mgr.AcquireRead("Test_diag")) { }
            Assert.Contains(log.Lines, l => l.Text.StartsWith("Storage open:") && l.Text.Contains("RssAnon n/a"));
            Assert.Contains(log.Lines, l => l.Text.StartsWith("Storage job-start:"));
            Assert.Contains(log.Lines, l => l.Text.StartsWith("Storage job-finish:"));
            Assert.DoesNotContain(log.Lines, l => l.Text.Contains("Test_diag") || l.Text.Contains(root));
            await mgr.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Diagnostics_NullLoggerAndBrokenReader_DoNotThrow()
    {
        var diag = new StorageDiagnostics(null, new FakeReader(_ => throw new IOException()));
        diag.LogStartup(3, 1);
        var root = NewRoot();
        try
        {
            var mgr = new DbManager(new DataRootResolver(root), diagnostics: diag);
            await mgr.CreateAsync("Test_x", CancellationToken.None);
            await mgr.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Warns_WhenMoreThan64L0Files()
    {
        var root = NewRoot();
        try
        {
            var log = new CaptureLogger<StorageDiagnostics>();
            var mgr = new DbManager(new DataRootResolver(root), diagnostics: new StorageDiagnostics(log, new FakeReader(_ => null)));
            await mgr.CreateAsync("Test_l0", CancellationToken.None);
            for (int i = 0; i < 66; i++)
            {
                int n = i;
                await mgr.WithWriteAsync("Test_l0", DbOpenMode.Bulk, (db, _) =>
                { db.Import(new[] { "word" + n, "other" + n }, null, null, threads: 1); return Task.FromResult(0); }, CancellationToken.None);
            }
            Assert.Contains(log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("compaction recommended"));
            await mgr.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WebPath_FreshInstall_ImportsKeepL0_CompactJobShrinks_ReadsCorrect()
    {
        var root = NewRoot();
        try
        {
            var mgr = new DbManager(new DataRootResolver(root));
            await mgr.CreateAsync("Test_fresh", CancellationToken.None);
            for (int i = 0; i < 6; i++)
            {
                int n = i;
                await mgr.WithWriteAsync("Test_fresh", DbOpenMode.Bulk, (db, _) =>
                { db.Import(Enumerable.Range(n * 100, 100).Select(x => "pw" + x), null, null, threads: 1); return Task.FromResult(0); }, CancellationToken.None);
            }
            long before;
            using (var lease = mgr.AcquireRead("Test_fresh"))   // lazy/reopened read handle: auto-compaction off
            {
                before = lease.Db.NumFilesAtLevel0();
                Assert.True(before >= 6, $"L0 {before}");
                Assert.Equal(600, lease.Db.CountRange(null, null));
            }
            await Task.Delay(500);
            using (var lease = mgr.AcquireRead("Test_fresh")) Assert.Equal(before, lease.Db.NumFilesAtLevel0());
            await mgr.WithWriteAsync("Test_fresh", DbOpenMode.Read, (db, _) => { db.Compact(); return Task.FromResult(0); }, CancellationToken.None);
            using (var lease = mgr.AcquireRead("Test_fresh"))
            {
                Assert.True(lease.Db.NumFilesAtLevel0() <= 1, $"after compact L0 {lease.Db.NumFilesAtLevel0()}");
                Assert.True(lease.Db.Contains("pw42"));
                Assert.Equal(600, lease.Db.CountRange(null, null));
            }
            await mgr.StopAsync(CancellationToken.None);
        }
        finally { Directory.Delete(root, true); }
    }
}
