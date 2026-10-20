using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Jobs;

public enum JobKind { Import, Compact, Search }
public enum JobStatus { Queued, Running, Completed, Failed, Cancelled }

/// <summary>JSON shape of a job (what SSE frames and the job endpoints return).</summary>
public sealed record JobSnapshot(
    string Id, string Kind, string Db, string? Source, string Status,
    long LinesRead, long BytesRead, long BytesTotal,
    long EstCountBefore, long EstCountAfter, long EstNewEntries, int Errors,
    DateTimeOffset QueuedAt, DateTimeOffset? StartedAt, DateTimeOffset? EndedAt, string? Error,
    string? Note = null, ImportMatch? DuplicateOf = null,
    // Search-only fields (unused/zero for Import/Compact). Matches is always null in a snapshot: the
    // match list can be huge, so it is paged out of the spool (GET /api/jobs/{id}/matches) instead
    // of riding every SSE frame. Capped means a server limit (low disk) stopped the scan; see Note.
    long ScannedCount = 0, long MatchedCount = 0, IReadOnlyList<string>? Matches = null, bool Capped = false);

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
    private string? _note, _sha;
    private long _size;
    private ImportMatch? _duplicateOf;
    private bool _forcedOverride;
    private long _scanned, _matched;
    private MatchSpool? _spool;
    private bool _capped;

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
    /// <summary>The user chose to import even if identical content was already imported.</summary>
    public bool Force { get; init; }

    /// <summary>Set on the compact jobs of one scheduled run (shared id); null = started by hand.</summary>
    public string? RunId { get; init; }

    /// <summary>SHA-256 (lower hex) of the raw source bytes; known at upload time, or after the pre-pass for server-side files.</summary>
    public string? ContentSha256 { get { lock (_gate) return _sha; } }
    public long SizeBytes { get { lock (_gate) return _size; } }
    public void SetContent(string sha256, long sizeBytes) { lock (_gate) { _sha = sha256; _size = sizeBytes; } }

    /// <summary>Informational message shown with the job (e.g. "changed file"); not an error.</summary>
    public string? Note { get { lock (_gate) return _note; } set { lock (_gate) _note = value; } }
    public ImportMatch? DuplicateOf { get { lock (_gate) return _duplicateOf; } set { lock (_gate) _duplicateOf = value; } }
    /// <summary>True when a duplicate warning was overridden with force.</summary>
    public bool ForcedOverride { get { lock (_gate) return _forcedOverride; } set { lock (_gate) _forcedOverride = value; } }

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

    /// <summary>Search progress: how many keys have been scanned so far (counter, not a percentage).</summary>
    public void SetScanned(long scanned) { lock (_gate) _scanned = scanned; }

    /// <summary>
    /// The on-disk match list of a search job (null for other kinds). Memory per job is only a sparse
    /// index; the match text lives in the spool file, so there is no result ceiling.
    /// </summary>
    public MatchSpool? Spool { get { lock (_gate) return _spool; } set { lock (_gate) _spool = value; } }

    /// <summary>Publishes the running match count after a chunk was committed to the spool.
    /// Capped means a server limit (e.g. low disk) stopped the scan early; the reason goes in Note.</summary>
    public void SetMatched(long matched, bool capped) { lock (_gate) { _matched = matched; _capped = capped; } }

    /// <summary>Frees job-held resources (the spool file). Idempotent; called on registry eviction.</summary>
    public void Dispose() => Spool?.Dispose();

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
                QueuedAt, _started, _ended, _error, _note, _duplicateOf,
                _scanned, _matched, null, _capped);
    }
}
