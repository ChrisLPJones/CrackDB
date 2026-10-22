using HoarderDB.Web.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable xUnit1031

namespace HoarderDB.Web.Tests;

/// <summary>Small hardening fixes (shutdown with a stuck reader, spool directory mode). Throwaway Test_&lt;guid&gt; roots only.</summary>
public sealed class HardeningTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());

    public HardeningTests()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        Directory.CreateDirectory(_root);
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch (IOException) { } }

    private sealed class CaptureLog<T> : ILogger<T>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel == LogLevel.Warning) lock (Lines) Lines.Add(formatter(state, exception)); }
    }

    [Fact]
    public void CloseAll_AtDeadlineWithActiveReader_SkipsDisposeAndWarns()
    {
        HoarderDbWebFactory.Seed(Path.Combine(_root, "Test_stuck"), new[] { "alpha", "beta" });
        var log = new CaptureLog<DbManager>();
        var mgr = new DbManager(new DataRootResolver(_root), logger: log);
        var lease = mgr.AcquireRead("Test_stuck");
        mgr.StopAsync(CancellationToken.None).Wait();   // waits the full 10 s deadline for the reader
        Assert.True(lease.Db.Contains("alpha"));        // handle was not disposed under the reader
        lock (log.Lines)
        {
            Assert.Single(log.Lines);
            Assert.Matches(@"^Shutdown deadline reached with \d+ active reader\(s\); skipping close of one database handle\.$", log.Lines[0]);
            Assert.DoesNotContain("Test_stuck", log.Lines[0]);
        }
        lease.Dispose();
        mgr.DisposeAsync().AsTask().Wait();
    }

    [Fact]
    public void SearchSpool_TryPrepare_TightensAnExistingWideDirectory()
    {
        if (OperatingSystem.IsWindows()) return;
        var spool = new SearchSpool(new DataRootResolver(_root), NullLogger<SearchSpool>.Instance);
        Directory.CreateDirectory(spool.Dir);
        File.SetUnixFileMode(spool.Dir, (UnixFileMode)0b111_111_111);   // 0777
        Assert.True(spool.TryPrepare());
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(spool.Dir));
    }
}
