using System.Threading.Channels;
using CrackDB.Web.Storage;

namespace CrackDB.Web.Jobs;

/// <summary>
/// The single global serialized worker. One job at a time bounds import RAM (a bulk-load handle is
/// several GB) and guarantees a compact never overlaps an import. Registered AFTER DbManager so that,
/// on shutdown, this StopAsync runs first (hosted services stop in reverse order): it cancels the
/// running job and waits for WithWriteAsync to unwind (flush, reopen Read, release the gate) before
/// DbManager closes the handles.
/// </summary>
public sealed class JobQueue(
    JobRegistry registry, IDbManager dbs, IncomingPathResolver incoming,
    IImportProgressHook hook, ILogger<JobQueue> log) : IHostedService
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

    /// <summary>Request cancellation. Imports stop cooperatively; a running compact cannot be cancelled.</summary>
    public JobSnapshot Cancel(Job job)
    {
        if (job.TryCancelQueued()) Cleanup(job);
        else if (job.Kind == JobKind.Import && !job.IsTerminal)
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
            if (job.Kind == JobKind.Import) await ImportRunner.RunAsync(job, dbs, hook, linked.Token);
            else await CompactRunner.RunAsync(job, dbs, linked.Token);
            // An import that was cancelled returned normally with a partial, flushed result.
            job.Finish(job.Kind == JobKind.Import && linked.IsCancellationRequested
                ? JobStatus.Cancelled : JobStatus.Completed);
        }
        catch (OperationCanceledException) { job.Finish(JobStatus.Cancelled); }
        catch (Exception ex)
        {
            // Full detail goes to the log only; the job carries a fixed message (no paths, no data).
            log.LogError(ex, "Job {Id} ({Kind} {Db}) failed", job.Id, job.Kind, job.DbName);
            job.Finish(JobStatus.Failed, ex switch
            {
                DbBusyException => "Database is busy.",
                DbNotFoundException => "Database not found.",
                IOException => "I/O error while reading the source or writing the database.",
                _ => "The job failed. See the server log.",
            });
        }
        finally { Cleanup(job); }
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
