using System.Security.Cryptography;
using System.Text;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

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
public enum ImportOutcome { Imported, Skipped }

/// <summary>The server-side source changed while it was being hashed (fixed message, no path).</summary>
public sealed class SourceChangedException() : IOException("The source file changed while it was being read.");

public static class ImportRunner
{
    public const int HookEvery = 1024;
    private const int BufferBytes = 1 << 20;
    /// <summary>Longest line imported. Longer ones are skipped (counted as errors) without being buffered.</summary>
    public const int MaxLineChars = 8 * 1024;

    /// <summary>
    /// Opens the source once (so the bytes hashed are the bytes imported), hashes it when the hash is not
    /// already known (server-side files; uploads are hashed while they stream in), skips identical content
    /// that was already imported unless forced, then streams it into the db inside the write window.
    /// The hash pass runs BEFORE the write window: the db stays readable and hashing never overlaps the
    /// import write buffers.
    /// </summary>
    public static async Task<ImportOutcome> RunAsync(Job job, IDbManager dbs, IImportHistory history,
        IImportProgressHook hook, CancellationToken ct)
    {
        var path = job.SourcePath ?? throw new InvalidOperationException("Import job has no source.");
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            BufferBytes, FileOptions.SequentialScan);
        try
        {
            if (await Task.Run(() => PrepareSource(job, history, path, fs, ct), CancellationToken.None) is { } early)
                return early;

            await dbs.WithWriteAsync(job.DbName, DbOpenMode.Bulk, (db, wct) => Task.Run(() =>
            {
                job.SetTotal(fs.Length, db.EstimatedCount());
                using var reader = new StreamReader(fs, new UTF8Encoding(false), true, BufferBytes, leaveOpen: true);
                long read = db.Import(Lines(job, reader, fs, hook, wct), job.Min, job.Max);
                job.SetProgress(read, fs.Position);
                job.SetAfter(db.EstimatedCount());
                return 0;
            }, CancellationToken.None), ct, allowCreate: true);
            return ImportOutcome.Imported;
        }
        finally { fs.Dispose(); }
    }

    /// <summary>Returns a non-null outcome when the import must not proceed (duplicate skipped, or cancelled while hashing).</summary>
    private static ImportOutcome? PrepareSource(Job job, IImportHistory history, string path, FileStream fs, CancellationToken ct)
    {
        if (job.ContentSha256 is null)
        {
            var length = fs.Length;
            var mtime = File.GetLastWriteTimeUtc(path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buf = new byte[1 << 16];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            {
                if (ct.IsCancellationRequested) return ImportOutcome.Imported;   // caller finishes it as cancelled
                hash.AppendData(buf, 0, n);
            }
            if (fs.Length != length || File.GetLastWriteTimeUtc(path) != mtime)
                throw new SourceChangedException();
            job.SetContent(Convert.ToHexStringLower(hash.GetHashAndReset()), length);
            fs.Seek(0, SeekOrigin.Begin);
        }

        var check = history.Check(job.DbName, job.ContentSha256!, job.Source, job.Min, job.Max);
        if (check.Duplicate is { } dup)
        {
            if (!job.Force)
            {
                job.DuplicateOf = ToMatch(dup);
                return ImportOutcome.Skipped;
            }
            job.ForcedOverride = true;
        }
        job.Note ??= check.Note;
        return null;
    }

    public static ImportMatch ToMatch(ImportRecord r) => new(r.ImportedAt, r.Source, r.JobId, r.LinesRead, r.Status);

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
