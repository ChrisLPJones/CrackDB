using System.Buffers;
using System.Text;
using System.Text.Unicode;
using System.Threading.Channels;
using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Endpoints;

/// <summary>
/// The body of a streamed export: ONE dedicated reader thread walks a scan cursor and fills pooled byte chunks into a
/// bounded queue while the async handler only awaits the socket writes, so reading and sending overlap (before, the
/// two strictly alternated). Output is byte-for-byte what <c>Range()</c> joined with "\n" gives (UTF-8, no BOM):
/// keys that are valid UTF-8 in a plain UTF-8 database are copied as they are; anything else goes through the string
/// decode/re-encode (U+FFFD replacement), exactly as before.
/// Lifecycle rule: the reader (and its cursor) is always finished before <see cref="RunAsync"/> returns, so the caller
/// may release the read lease right after. Memory is bounded: <see cref="QueueDepth"/> queued chunks plus the one being
/// filled and the one being written.
/// </summary>
internal static class ExportPipeline
{
    /// <summary>Chunk size; above Kestrel's 64 KiB response-pipe threshold so each write is worth one await.</summary>
    internal static int ChunkBytes = 512 * 1024;
    internal const int QueueDepth = 4;

    /// <summary>Test seam: called on the reader thread before each chunk is queued (argument = chunk index); may throw to inject a fault.</summary>
    internal static Action<long>? ReaderFaultHook;

    // Test diagnostics: chunk arrays currently rented from the pool, and the peak since the last reset.
    private static long _inFlight, _peak;
    internal static long PeakChunksInFlight => Interlocked.Read(ref _peak);
    internal static long ChunksInFlight => Interlocked.Read(ref _inFlight);
    internal static void ResetPeak() => Interlocked.Exchange(ref _peak, Interlocked.Read(ref _inFlight));

    private readonly record struct Chunk(byte[] Buf, int Len);

    private static byte[] Rent(int size)
    {
        var b = ArrayPool<byte>.Shared.Rent(size);
        long n = Interlocked.Increment(ref _inFlight);
        long p;
        while (n > (p = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, n, p) != p) { }
        return b;
    }

    private static void Return(byte[] b)
    {
        Interlocked.Decrement(ref _inFlight);
        ArrayPool<byte>.Shared.Return(b);
    }

    /// <summary>
    /// Streams the export to <paramref name="body"/>. A reader fault or a shutdown calls <paramref name="abort"/> (reset the
    /// connection: the chunked body is NOT finished, so the client cannot mistake a truncated file for a complete one).
    /// A client disconnect just ends quietly. Log lines are fixed text: never a password, never a key.
    /// </summary>
    public static async Task RunAsync(DbLease lease, Stream body, int? min, int? max, long? limit,
        ILogger? log, Action abort, CancellationToken ct)
    {
        var ch = Channel.CreateBounded<Chunk>(new BoundedChannelOptions(QueueDepth)
        { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        using var stop = new CancellationTokenSource();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        try
        {
            var db = lease.Db;
            var thread = new Thread(() => ReaderMain(db, min, max, limit, ch.Writer, stop.Token, done))
            { IsBackground = true, Name = "export-reader" };
            thread.Start();
            started = true;

            while (await ch.Reader.WaitToReadAsync(ct))
            {
                while (ch.Reader.TryRead(out var c))
                {
                    try
                    {
                        if (lease.Closing)
                        {
                            log?.LogWarning("Export stopped: the server is shutting down.");
                            abort();
                            return;
                        }
                        ct.ThrowIfCancellationRequested();
                        await body.WriteAsync(c.Buf.AsMemory(0, c.Len), ct);
                    }
                    finally { Return(c.Buf); }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* client went away */ }
        catch (Exception e)
        {
            // Headers are already sent, so there is no 500: reset the connection. Type name only, no message.
            log?.LogError("Export failed mid-stream ({ExceptionType}); connection aborted.", e.GetType().Name);
            abort();
        }
        finally
        {
            stop.Cancel();
            if (started) await done.Task;   // reader + cursor are gone before the caller releases the lease
            while (ch.Reader.TryRead(out var c)) Return(c.Buf);
        }
    }

    private static void ReaderMain(WordlistDatabase db, int? min, int? max, long? limit,
        ChannelWriter<Chunk> queue, CancellationToken stop, TaskCompletionSource done)
    {
        byte[]? buf = null;
        try
        {
            using var cur = db.OpenScan(min, max);
            cur.SeekTo(null);
            int chunkSize = ChunkBytes;
            buf = Rent(chunkSize);
            int n = 0;
            long produced = 0, chunkIndex = 0;
            var utf8 = new UTF8Encoding(false);

            void Flush()
            {
                if (n == 0) return;
                ReaderFaultHook?.Invoke(chunkIndex++);
                var c = new Chunk(buf!, n);
                while (!queue.TryWrite(c))
                    queue.WaitToWriteAsync(stop).AsTask().GetAwaiter().GetResult();   // dedicated thread: blocking is fine
                buf = null;
                n = 0;
            }

            while (cur.MoveNext())
            {
                if (stop.IsCancellationRequested) return;
                var key = cur.PasswordBytes;
                bool copy = cur.IsPlainUtf8 && Utf8.IsValid(key);
                string? word = copy ? null : cur.Word;
                int need = (copy ? key.Length : utf8.GetByteCount(word!)) + 1;
                if (buf == null || n + need > buf.Length)
                {
                    Flush();
                    if (buf != null && need > buf.Length) { Return(buf); buf = null; }
                    buf ??= Rent(Math.Max(chunkSize, need));
                }
                if (copy) key.CopyTo(buf.AsSpan(n)); else utf8.GetBytes(word!, buf.AsSpan(n));
                n += need - 1;
                buf[n++] = (byte)'\n';
                if (limit.HasValue && ++produced >= limit.Value) break;
            }
            Flush();
            queue.TryComplete();
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { queue.TryComplete(); }
        catch (Exception e) { queue.TryComplete(e); }
        finally
        {
            if (buf != null) Return(buf);
            done.TrySetResult();
        }
    }
}
