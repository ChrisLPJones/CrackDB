using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

/// <summary>
/// Compaction via the normal write window (Read mode; auto-compaction is off on that handle, the explicit
/// manual CompactRange does not need the auto trigger). CompactRange cannot
/// be cancelled, so a running compact ignores cancel; it can only be cancelled while queued.
/// </summary>
public static class CompactRunner
{
    public static Task RunAsync(Job job, IDbManager dbs, CancellationToken ct) =>
        dbs.WithWriteAsync(job.DbName, DbOpenMode.Read, (db, _) => Task.Run(() => { db.Compact(); return 0; }, CancellationToken.None), ct);
}
