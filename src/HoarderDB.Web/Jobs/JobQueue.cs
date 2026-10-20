using System.Threading.Channels;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

/// <summary>
/// The single global serialized worker. One job at a time bounds import RAM (a bulk-load handle is
/// several GB) and guarantees a compact never overlaps an import. Registered AFTER DbManager so that,
/// on shutdown, this StopAsync runs first (hosted services stop in reverse order): it cancels the
/// running job and waits for WithWriteAsync to unwind (flush, reopen Read, release the gate) before
/// DbManager closes the handles.
/// </summary>
public sealed class JobQueue(
    JobRegistry registry, IDbManager dbs, IncomingPathResolver incoming,
    IImportProgressHook hook, IImportHistory history, ILogger<JobQueue> log,
    CompactionHistory? compactionHistory = null) : IHostedService
{
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _stopping = new();
    private Task _loop = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        CleanOrphanUploads();
        _loop = Task.Run(LoopAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();               // running import stops at its next line; reopen/flush follow
        _channel.Writer.TryComplete();
        try { await _loop.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) { log.LogWarning("Shutdown timed out waiting for the running job to unwind."); }
    }

    public void Enqueue(Job job)
    {
        registry.Add(job);
        if (!_channel.Writer.TryWrite(job.Id))
        {
            job.Finish(JobStatus.Cancelled, "Server is shutting down.");
            Cleanup(job);
        }
    }

    /// <summary>
    /// Request cancellation. Imports and searches stop cooperatively (checked every chunk); a running
    /// compact cannot be cancelled. A search job is never queued through this worker (see
    /// <see cref="SearchRunner"/>), but it shares this one cancel endpoint/route, so it is handled
    /// here too - Cts.Cancel() is all that's needed, SearchRunner.ExecuteAsync does its own cleanup.
    /// </summary>
    public JobSnapshot Cancel(Job job)
    {
        if (job.TryCancelQueued()) Cleanup(job);
        else if (job.Kind is JobKind.Import or JobKind.Search && !job.IsTerminal)
        {
            try { job.Cts.Cancel(); } catch (ObjectDisposedException) { /* finished in the meantime */ }
        }
        return job.Snapshot();
    }

    private async Task LoopAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync(_stopping.Token))
                while (_channel.Reader.TryRead(out var id))
                    if (registry.Get(id) is { } job) await ExecuteAsync(job);
        }
        catch (OperationCanceledException) { }
        // Anything still queued at shutdown never runs.
        while (_channel.Reader.TryRead(out var id))
            if (registry.Get(id) is { } job) { job.Finish(JobStatus.Cancelled, "Server is shutting down."); Cleanup(job); }
    }

    private async Task ExecuteAsync(Job job)
    {
        if (!job.TryStart()) return;   // cancelled while queued; Cancel() already cleaned up
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(job.Cts.Token, _stopping.Token);
        try
        {
            // Cheap safety net: refuse the job rather than risk an OOM crash if there isn't enough
            // headroom above the configured import budget. Checked for every job (import or
            // compact), since both run through the same bulk/read handle and memory budget.
            // Refused before it ever ran, so it never attempted work: no import-history record.
            if (!HeadroomGuard.HasHeadroom(dbs.ResolvedImportBudgetBytes, out var headroomReason))
            {
                job.Finish(JobStatus.Failed, headroomReason!);
                return;
            }
            var outcome = ImportOutcome.Imported;
            if (job.Kind == JobKind.Import) outcome = await ImportRunner.RunAsync(job, dbs, history, hook, linked.Token);
            else await CompactRunner.RunAsync(job, dbs, linked.Token);
            if (outcome == ImportOutcome.Skipped)
            {
                // Identical content already imported and not forced: no import, no history record.
                var when = job.DuplicateOf?.ImportedAt.UtcDateTime.ToString("yyyy-MM-dd");
                job.Finish(JobStatus.Cancelled, $"Skipped: identical content already imported on {when}.");
            }
            else
            {
                // An import that was cancelled returned normally with a partial, flushed result.
                var terminal = job.Kind == JobKind.Import && linked.IsCancellationRequested
                    ? JobStatus.Cancelled : JobStatus.Completed;
                Record(job, terminal);     // before Finish: a client that sees the terminal job already sees its entry
                job.Finish(terminal);
            }
        }
        catch (OperationCanceledException) { Record(job, JobStatus.Cancelled); job.Finish(JobStatus.Cancelled); }
        catch (Exception ex)
        {
            // Full detail goes to the log only; the job carries a fixed message (no paths, no data).
            log.LogError(ex, "Job {Id} ({Kind} {Db}) failed", job.Id, job.Kind, job.DbName);
            Record(job, JobStatus.Failed);
            job.Finish(JobStatus.Failed, ex switch
            {
                DbBusyException => "Database is busy.",
                DbNotFoundException => "Database not found.",
                SourceChangedException => "The source file changed while it was being read.",
                IOException => "I/O error while reading the source or writing the database.",
                _ => "The job failed. See the server log.",
            });
        }
        finally { RecordCompaction(job); Cleanup(job); }
    }

    /// <summary>Writes the import's history entry. Only imports whose content hash is known are recorded; advisory, never fatal.</summary>
    private void Record(Job job, JobStatus terminal)
    {
        if (job.Kind != JobKind.Import || job.ContentSha256 is not { } sha || job.DuplicateOf != null) return;
        try
        {
            var s = job.Snapshot();
            history.Append(job.DbName, new ImportRecord(
                Guid.NewGuid().ToString("N"), job.Id, job.Source ?? "", sha, job.SizeBytes,
                DateTimeOffset.UtcNow, s.StartedAt, terminal.ToString().ToLowerInvariant(),
                job.Min, job.Max, s.LinesRead, s.Errors, s.EstNewEntries, job.ForcedOverride));
        }
        catch (Exception ex) { log.LogWarning("Could not record import history for job {Id} ({Kind})", job.Id, ex.GetType().Name); }
    }

    /// <summary>Appends a finished compact job to the compaction history (advisory, never fatal; terminal on every path here).</summary>
    private void RecordCompaction(Job job)
    {
        if (job.Kind != JobKind.Compact || compactionHistory is null) return;
        try
        {
            var s = job.Snapshot();
            if (s.StartedAt is not { } started || s.EndedAt is not { } ended) return;
            compactionHistory.Append(new CompactionEntry(job.RunId, job.DbName, s.Status, started, ended));
        }
        catch (Exception ex) { log.LogWarning("Could not record compaction history for job {Id} ({Kind})", job.Id, ex.GetType().Name); }
    }

    private void Cleanup(Job job)
    {
        if (job.DeleteSourceWhenDone && job.SourcePath != null)
        {
            try { File.Delete(job.SourcePath); }
            catch (Exception ex) { log.LogWarning(ex, "Could not delete staged upload for job {Id}", job.Id); }
        }
        job.Cts.Dispose();
        registry.MarkFinished(job);
    }

    // Upload staging files only exist for the lifetime of a job, and jobs do not survive a restart.
    private void CleanOrphanUploads()
    {
        if (!incoming.Configured) return;
        try
        {
            var dir = Directory.CreateDirectory(incoming.UploadDir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            foreach (var f in dir.EnumerateFiles()) f.Delete();
        }
        catch (Exception ex) { log.LogWarning(ex, "Could not prepare the upload staging directory"); }
    }
}
