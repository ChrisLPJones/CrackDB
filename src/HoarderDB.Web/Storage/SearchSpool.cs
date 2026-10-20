using System.Text;
using System.Text.RegularExpressions;

namespace HoarderDB.Web.Storage;

/// <summary>A match file was deleted (cancelled/failed/expired/evicted job); the API answers 410.</summary>
public sealed class SearchResultsGoneException() : Exception("Results are no longer available.");

/// <summary>
/// One search job's matches on disk: append-only file of records <c>[4-byte length][UTF-8 bytes]</c>
/// (immune to any character in a password). Memory is a sparse index (offset of every 1024th record), so
/// a page is a seek plus at most 1023 skipped records. Readers never read past the committed length, which
/// is only advanced after a chunk is written and flushed.
/// </summary>
public sealed class MatchSpool : IDisposable
{
    public const int IndexEvery = 1024;
    private const int MaxRecordBytes = 64 * 1024 * 1024;   // sanity bound while reading, never produced by the writer

    private readonly object _gate = new();
    private readonly Action<MatchSpool> _onDispose;
    private FileStream? _writer;
    private readonly List<long> _index = new();
    private long _committedBytes, _count;
    private bool _disposed;

    internal MatchSpool(string id, string path, FileStream writer, Action<MatchSpool> onDispose)
    {
        Id = id; Path = path; _writer = writer; _onDispose = onDispose;
    }

    public string Id { get; }
    internal string Path { get; }
    public DateTimeOffset? FinishedAt { get; private set; }
    public long Count { get { lock (_gate) return _count; } }
    public bool IsDisposed { get { lock (_gate) return _disposed; } }

    /// <summary>Appends a chunk, flushes, then publishes it to readers. Single writer (the one search thread).</summary>
    public void Append(IReadOnlyList<string> words)
    {
        if (words.Count == 0) return;
        FileStream w;
        long baseBytes, baseCount;
        lock (_gate)
        {
            if (_disposed || _writer is null) throw new SearchResultsGoneException();
            (w, baseBytes, baseCount) = (_writer, _committedBytes, _count);
        }
        using var buf = new MemoryStream();
        var newIndex = new List<long>();
        Span<byte> len = stackalloc byte[4];
        for (int i = 0; i < words.Count; i++)
        {
            if ((baseCount + i) % IndexEvery == 0) newIndex.Add(baseBytes + buf.Length);
            var bytes = Encoding.UTF8.GetBytes(words[i]);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(len, bytes.Length);
            buf.Write(len);
            buf.Write(bytes);
        }
        w.Write(buf.GetBuffer(), 0, (int)buf.Length);
        w.Flush();
        lock (_gate)
        {
            if (_disposed) throw new SearchResultsGoneException();
            _index.AddRange(newIndex);
            _committedBytes += buf.Length;
            _count += words.Count;
        }
    }

    /// <summary>The scan is over: close the writer (file stays readable) and start the expiry clock.</summary>
    public void CompleteWriting(DateTimeOffset now)
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch (IOException) { }
            _writer = null;
            FinishedAt = now;
        }
    }

    /// <summary>
    /// A page: records [after, after+limit), stopping early once <paramref name="maxBytes"/> of text was collected
    /// (always at least one item). <c>Next</c> = after + items.Count.
    /// </summary>
    public (long Total, List<string> Items) ReadPage(long after, int limit, int maxBytes)
    {
        long committed, count, start;
        lock (_gate)
        {
            if (_disposed) throw new SearchResultsGoneException();
            (committed, count) = (_committedBytes, _count);
            if (after >= count) return (count, new List<string>());
            start = _index[(int)(after / IndexEvery)];
        }
        var items = new List<string>();
        try
        {
            using var fs = OpenRead();
            fs.Seek(start, SeekOrigin.Begin);
            long pos = start, record = (after / IndexEvery) * IndexEvery, collected = 0;
            var lenBuf = new byte[4];
            while (pos < committed && items.Count < limit)
            {
                fs.ReadExactly(lenBuf);
                int n = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (n < 0 || n > MaxRecordBytes) throw new InvalidDataException("Corrupt match file.");
                pos += 4 + n;
                if (record < after) { fs.Seek(n, SeekOrigin.Current); record++; continue; }
                var data = new byte[n];
                fs.ReadExactly(data);
                record++;
                items.Add(Encoding.UTF8.GetString(data));
                collected += n;
                if (collected >= maxBytes) break;
            }
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new SearchResultsGoneException(); }
        return (count, items);
    }

    /// <summary>Every committed record as raw UTF-8 bytes, for the download stream (no full buffering).</summary>
    internal async Task CopyAllLinesAsync(Stream output, CancellationToken ct)
    {
        long committed;
        lock (_gate)
        {
            if (_disposed) throw new SearchResultsGoneException();
            committed = _committedBytes;
        }
        try
        {
            await using var fs = OpenRead();
            using var rd = new BufferedStream(fs, 256 * 1024);
            using var outBuf = new MemoryStream();
            var lenBuf = new byte[4];
            var data = new byte[1024];
            long pos = 0;
            while (pos < committed)
            {
                await rd.ReadExactlyAsync(lenBuf, ct);
                int n = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(lenBuf);
                if (n < 0 || n > MaxRecordBytes) throw new InvalidDataException("Corrupt match file.");
                if (data.Length < n) data = new byte[Math.Max(n, data.Length * 2)];
                await rd.ReadExactlyAsync(data.AsMemory(0, n), ct);
                pos += 4 + n;
                outBuf.Write(data, 0, n);
                outBuf.WriteByte((byte)'\n');
                if (outBuf.Length >= 256 * 1024)
                {
                    await output.WriteAsync(outBuf.GetBuffer().AsMemory(0, (int)outBuf.Length), ct);
                    outBuf.SetLength(0);
                }
            }
            if (outBuf.Length > 0) await output.WriteAsync(outBuf.GetBuffer().AsMemory(0, (int)outBuf.Length), ct);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { throw new SearchResultsGoneException(); }
    }

    private FileStream OpenRead() =>
        new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>Closes the writer and deletes the file (one exact path, never a pattern). Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try { _writer?.Dispose(); } catch (IOException) { }
            _writer = null;
        }
        try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _onDispose(this);
    }
}

/// <summary>
/// Server-owned temp area for search results: <c>&lt;DataRoot&gt;/.search-tmp/&lt;job id&gt;.matches</c>. A dot-name can
/// never be a database (the name allowlist requires an alphanumeric first character, and listing skips it).
/// File names come only from the server-generated job GUID, never from request text. Everything in the
/// directory is wiped at startup and shutdown (jobs are in memory, so any file is an orphan after a restart);
/// finished results expire after <see cref="Ttl"/> or when the next search starts.
/// </summary>
public sealed partial class SearchSpool(DataRootResolver data, ILogger<SearchSpool> log) : IHostedService
{
    /// <summary>Stop a scan when free space on the spool volume drops below this: the DB volume needs headroom to compact.</summary>
    public const long ReserveBytes = 1L << 30;
    public static readonly TimeSpan Ttl = TimeSpan.FromHours(1);
    private static readonly TimeSpan JanitorEvery = TimeSpan.FromMinutes(5);

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdRegex();

    private readonly object _gate = new();
    private readonly Dictionary<string, MatchSpool> _live = new(StringComparer.Ordinal);
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public string Dir => System.IO.Path.Combine(data.Root, ".search-tmp");

    /// <summary>Creates the directory (0700) if needed; false if it cannot be trusted (a symlink) or created.</summary>
    public bool TryPrepare()
    {
        try
        {
            var dir = Dir;
            var info = new DirectoryInfo(dir);
            if (info.LinkTarget is not null) { log.LogError("Search spool directory is a symlink; refusing to use it."); return false; }
            if (!info.Exists)
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
                else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogError(e, "Search spool directory could not be created.");
            return false;
        }
    }

    public MatchSpool Create(string jobId)
    {
        if (!IdRegex().IsMatch(jobId)) throw new ArgumentException("Invalid job id.", nameof(jobId));
        var path = System.IO.Path.Combine(Dir, jobId + ".matches");
        lock (_gate)
        {
            var opts = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.ReadWrite | FileShare.Delete,
            };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            var fs = new FileStream(path, opts);
            var spool = new MatchSpool(jobId, path, fs, Forget);
            _live[jobId] = spool;
            return spool;
        }
    }

    private void Forget(MatchSpool s) { lock (_gate) _live.Remove(s.Id); }

    /// <summary>Deletes every known result set (a new search replaces the previous one; only one search runs at a time).</summary>
    public void DisposeAll()
    {
        MatchSpool[] all;
        lock (_gate) all = _live.Values.ToArray();
        foreach (var s in all) s.Dispose();
    }

    /// <summary>
    /// Expires finished results older than <see cref="Ttl"/> and removes files in the spool directory that no job
    /// owns. Touches only <c>*.matches</c> directly inside the directory, one <c>File.Delete</c> per path.
    /// </summary>
    public void Sweep(DateTimeOffset now)
    {
        MatchSpool[] expired;
        lock (_gate) expired = _live.Values.Where(s => s.FinishedAt is { } f && now - f > Ttl).ToArray();
        foreach (var s in expired) s.Dispose();
        DeleteFiles(keepLive: true);
    }

    private void DeleteFiles(bool keepLive)
    {
        var dir = Dir;
        if (new DirectoryInfo(dir).LinkTarget is not null || !Directory.Exists(dir)) return;
        foreach (var f in Directory.EnumerateFiles(dir, "*.matches", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var id = System.IO.Path.GetFileNameWithoutExtension(f);
                lock (_gate) { if (keepLive && _live.ContainsKey(id)) continue; }
                File.Delete(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not delete an orphaned search result file.");
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try { DeleteFiles(keepLive: false); }   // every file is an orphan after a restart
        catch (Exception e) { log.LogWarning(e, "Search spool startup sweep failed."); }
        _stop = new CancellationTokenSource();
        var ct = _stop.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(JanitorEvery);
            try
            {
                while (await timer.WaitForNextTickAsync(ct))
                {
                    try { Sweep(DateTimeOffset.UtcNow); }
                    catch (Exception e) { log.LogWarning(e, "Search spool sweep failed."); }
                }
            }
            catch (OperationCanceledException) { }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_loop is not null) { try { await _loop.WaitAsync(cancellationToken); } catch (OperationCanceledException) { } }
        DisposeAll();
        try { DeleteFiles(keepLive: false); } catch (Exception e) { log.LogWarning(e, "Search spool shutdown sweep failed."); }
    }
}
