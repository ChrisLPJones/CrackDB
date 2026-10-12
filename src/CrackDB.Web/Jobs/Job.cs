namespace CrackDB.Web.Jobs;

public enum JobKind { Import, Compact }
public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>JSON shape of a job (what SSE frames and the job endpoints return).</summary>
public sealed record JobSnapshot(
    string Id, string Kind, string Db, string? Source, string Status,
    long LinesRead, long BytesRead, long BytesTotal,
    long EstCountBefore, long EstCountAfter, long EstNewEntries, int Errors,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Error);

/// <summary>
/// One queued/running/finished unit of work. Mutated by the worker, read by the endpoints, so
/// every access goes through the lock. Generic on purpose (Kind + counters) so other long-running
/// operations can be added later.
/// </summary>
public sealed class Job
{
    private readonly object _gate = new();
    private JobStatus _status = JobStatus.Queued;
    private long _lines, _bytes, _bytesTotal, _before, _after;
    private int _errors;
    private DateTimeOffset? _started, _ended;
    private string? _error;

    public Job(JobKind kind, string dbName, string? label)
    {
        Id = Guid.NewGuid().ToString("N");
        Kind = kind;
        DbName = dbName;
        Source = label;
        QueuedAt = DateTimeOffset.UtcNow;
    }

    public string Id { get; }
    public JobKind Kind { get; }
    public string DbName { get; }
    public string? Source { get; }                 // display label only: relative path or "upload:<name>"
    public DateTimeOffset QueuedAt { get; }

    // Never serialized: the resolved file to read and import limits.
    public string? SourcePath { get; init; }
    public bool DeleteSourceWhenDone { get; init; }
    public int? Min { get; init; }
    public int? Max { get; init; }

    /// <summary>Counts a skipped input (e.g. an overlong line) so it shows in the job's error count.</summary>
    public void AddError() { lock (_gate) _errors++; }

    public CancellationTokenSource Cts { get; } = new();

    public JobStatus Status { get { lock (_gate) return _status; } }
    public bool IsTerminal { get { lock (_gate) return IsTerminalStatus(_status); } }
    public static bool IsTerminalStatus(JobStatus s) => s is JobStatus.Completed or JobStatus.Failed or JobStatus.Cancelled;

    /// <summary>Queued to Running; false if it was cancelled while waiting.</summary>
    public bool TryStart()
    {
        lock (_gate)
        {
            if (_status != JobStatus.Queued) return false;
            _status = JobStatus.Running;
            _started = DateTimeOffset.UtcNow;
            return true;
        }
    }

    /// <summary>Cancel a job that has not started; false if it is already running or finished.</summary>
    public bool TryCancelQueued()
    {
        lock (_gate)
        {
            if (_status != JobStatus.Queued) return false;
            _status = JobStatus.Cancelled;
            _ended = DateTimeOffset.UtcNow;
            return true;
        }
    }

    public void SetTotal(long bytesTotal, long estBefore) { lock (_gate) { _bytesTotal = bytesTotal; _before = estBefore; } }
    public void SetProgress(long lines, long bytes) { lock (_gate) { _lines = lines; _bytes = bytes; } }
    public void SetAfter(long estAfter) { lock (_gate) _after = estAfter; }

    public void Finish(JobStatus terminal, string? error = null)
    {
        lock (_gate)
        {
            if (IsTerminalStatus(_status)) return;
            _status = terminal;
            _error = error;
            _ended = DateTimeOffset.UtcNow;
            if (terminal == JobStatus.Failed) _errors++;
        }
    }

    public JobSnapshot Snapshot()
    {
        lock (_gate)
            return new JobSnapshot(Id, Kind.ToString().ToLowerInvariant(), DbName, Source,
                _status.ToString().ToLowerInvariant(), _lines, _bytes, _bytesTotal,
                _before, _after, Math.Max(0, _after - _before), _errors,
                QueuedAt, _started, _ended, _error);
    }
}
