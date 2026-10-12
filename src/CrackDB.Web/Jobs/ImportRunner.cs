using System.Text;
using CrackDB.Web.Storage;

namespace CrackDB.Web.Jobs;

/// <summary>Test seam, called every <see cref="ImportRunner.HookEvery"/> lines. Default is a no-op.</summary>
public interface IImportProgressHook
{
    void OnLines(string dbName, long linesRead);
}

public sealed class NoopImportProgressHook : IImportProgressHook
{
    public void OnLines(string dbName, long linesRead) { }
}

/// <summary>
/// Streams a server-side file into a db through the engine's lazy-enumerable Import. The file is
/// never buffered. Cancellation is cooperative with no Core change: the line iterator simply stops
/// (yield break) when cancelled, so Import's producer finishes normally (CompleteAdding runs, workers
/// drain, Flush) and leaves a partial, valid, flushed import. The iterator must never throw on cancel.
/// </summary>
public static class ImportRunner
{
    public const int HookEvery = 1024;
    private const int BufferBytes = 1 << 20;
    /// <summary>Longest line imported. Longer ones are skipped (counted as errors) without being buffered.</summary>
    public const int MaxLineChars = 8 * 1024;

    public static async Task RunAsync(Job job, IDbManager dbs, IImportProgressHook hook, CancellationToken ct)
    {
        var path = job.SourcePath ?? throw new InvalidOperationException("Import job has no source.");
        await dbs.WithWriteAsync(job.DbName, DbOpenMode.Bulk, (db, wct) => Task.Run(() =>
        {
            var info = new FileInfo(path);
            job.SetTotal(info.Length, db.EstimatedCount());
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                BufferBytes, FileOptions.SequentialScan);
            using var reader = new StreamReader(fs, new UTF8Encoding(false), true, BufferBytes);
            long read = db.Import(Lines(job, reader, fs, hook, wct), job.Min, job.Max);
            job.SetProgress(read, fs.Position);
            job.SetAfter(db.EstimatedCount());
            return 0;
        }, CancellationToken.None), ct, allowCreate: true);
    }

    private static IEnumerable<string> Lines(Job job, StreamReader reader, FileStream fs,
        IImportProgressHook hook, CancellationToken ct)
    {
        long n = 0;
        var lines = new BoundedLineReader(reader, MaxLineChars);
        while (!ct.IsCancellationRequested && lines.TryReadLine(out var line, out var overlong))
        {
            n++;
            if (overlong) job.AddError();
            if ((n & (HookEvery - 1)) == 0)
            {
                job.SetProgress(n, fs.Position);
                hook.OnLines(job.DbName, n);
            }
            if (!overlong) yield return line!;
        }
        // Falling out of the loop (EOF or cancel) ends the sequence cleanly.
    }
}
