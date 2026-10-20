using System.Collections.Concurrent;

namespace HoarderDB.Web.Jobs;

/// <summary>In-memory job store with a bounded history of finished jobs. Not persisted across restarts.</summary>
public sealed class JobRegistry
{
    public const int MaxFinished = 50;

    private readonly ConcurrentDictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Queue<string> _finished = new();
    private readonly object _gate = new();

    public void Add(Job job) => _jobs[job.Id] = job;

    public Job? Get(string? id) => id != null && _jobs.TryGetValue(id, out var j) ? j : null;

    /// <summary>Record a terminal job in the history ring and evict the oldest beyond the bound.</summary>
    public void MarkFinished(Job job)
    {
        lock (_gate)
        {
            _finished.Enqueue(job.Id);
            while (_finished.Count > MaxFinished)
                if (_jobs.TryRemove(_finished.Dequeue(), out var old)) old.Dispose();   // frees a search job's spool file
        }
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<JobSnapshot> List() =>
        _jobs.Values.Select(j => j.Snapshot()).OrderByDescending(s => s.QueuedAt).ToList();
}
