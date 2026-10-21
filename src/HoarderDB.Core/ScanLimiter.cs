using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HoarderDB.Core.Tests")]
[assembly: InternalsVisibleTo("HoarderDB.Web.Tests")]

namespace HoarderDB;

/// <summary>
/// Process-wide budget of scan worker threads (the web app's <c>Storage:ScanThreads</c>). Every parallel scan
/// worker holds one permit while it runs, so several heavy scans (breakdown, exact count, stats) together can
/// never run more than <see cref="Cap"/> scan threads, whatever the number of open databases. One instance is
/// shared by all databases of a process (the web app's DbManager creates it); a standalone handle uses
/// <see cref="Default"/>. Operator config only, never client input.
/// </summary>
public sealed class ScanLimiter
{
    /// <summary>Sanity ceiling for a configured value (a typo like 4000 must not spawn thousands of threads).</summary>
    public const int MaxCap = 64;

    private readonly object _gate = new();
    private int _free;

    public int Cap { get; }

    public ScanLimiter(int cap)
    {
        if (cap < 1 || cap > MaxCap) throw new ArgumentOutOfRangeException(nameof(cap), $"Scan threads must be 1..{MaxCap}.");
        Cap = cap;
        _free = cap;
    }

    /// <summary>Default when nothing is configured: one core is left for the web server / Kestrel.</summary>
    public static int AutoThreads(int processors) => Math.Clamp(processors - 1, 1, MaxCap);

    /// <summary>Shared by standalone handles (CLI, tests) that were not given a limiter.</summary>
    public static ScanLimiter Default { get; } = new(AutoThreads(Environment.ProcessorCount));

    /// <summary>Permits not currently held (test/diagnostic).</summary>
    public int Free { get { lock (_gate) return _free; } }

    /// <summary>
    /// Takes up to <paramref name="want"/> permits (at least one): waits only while none at all is free,
    /// so a scan queued behind another one still runs, just narrower. Cancellable while waiting.
    /// </summary>
    public int AcquireUpTo(int want, CancellationToken ct)
    {
        want = Math.Clamp(want, 1, Cap);
        lock (_gate)
        {
            while (_free == 0)
            {
                ct.ThrowIfCancellationRequested();
                Monitor.Wait(_gate, 100);
            }
            int got = Math.Min(want, _free);
            _free -= got;
            return got;
        }
    }

    public void Release(int n)
    {
        if (n <= 0) return;
        lock (_gate)
        {
            _free += n;
            Monitor.PulseAll(_gate);
        }
    }
}
