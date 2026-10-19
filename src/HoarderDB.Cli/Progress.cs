using System.Diagnostics;

namespace HoarderDB;

/// <summary>
/// A single-line, self-overwriting progress bar for imports. Progress is measured
/// by bytes read from the source file, so it stays accurate on multi-hundred-GB
/// inputs. Rendering is throttled and written to stderr, so it never mixes into
/// stdout or an exported wordlist.
/// </summary>
public sealed class ByteProgressBar
{
    private const int Width = 30;
    private readonly long _total;
    private readonly bool _enabled;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastRenderMs = -1000;
    private int _lastLen;

    public ByteProgressBar(long totalBytes, bool enabled)
    {
        _total = totalBytes;
        _enabled = enabled && totalBytes > 0;
    }

    public void Report(long bytes, long lines)
    {
        if (!_enabled) return;
        long ms = _sw.ElapsedMilliseconds;
        if (ms - _lastRenderMs < 200) return;   // at most ~5 redraws/sec
        _lastRenderMs = ms;
        Render(bytes, lines, final: false);
    }

    public void Finish(long bytes, long lines)
    {
        if (!_enabled) return;
        Render(bytes, lines, final: true);
        Console.Error.WriteLine();
    }

    private void Render(long bytes, long lines, bool final)
    {
        double frac = Math.Clamp((double)bytes / _total, 0, 1);
        if (final) frac = 1;
        int filled = (int)(frac * Width);
        string bar = new string('#', filled) + new string('-', Width - filled);

        double secs = Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
        double mbps = bytes / (1024.0 * 1024.0) / secs;
        string eta = final ? "00:00:00"
                   : (frac > 0 && mbps > 0 ? Hms((_total - bytes) / (1024.0 * 1024.0) / mbps) : "--:--:--");

        string msg = $"\r[{bar}] {frac * 100,5:0.0}%  {Gb(bytes)}/{Gb(_total)}  {mbps,6:0.0} MB/s  ETA {eta}  {lines:N0} lines";
        // Pad to erase any leftover characters from a previously longer line.
        if (msg.Length < _lastLen) msg += new string(' ', _lastLen - msg.Length);
        _lastLen = msg.Length;
        Console.Error.Write(msg);
    }

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.00") + " GB";

    private static string Hms(double secs)
    {
        if (double.IsNaN(secs) || double.IsInfinity(secs) || secs < 0) return "--:--:--";
        var t = TimeSpan.FromSeconds(secs);
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }
}

/// <summary>
/// A single-line progress counter for operations without a known total (export).
/// Shows how many items have been produced and the current rate.
/// </summary>
public sealed class CountProgress
{
    private readonly bool _enabled;
    private readonly string _noun;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastRenderMs = -1000;
    private int _lastLen;

    public CountProgress(bool enabled, string noun)
    {
        _enabled = enabled;
        _noun = noun;
    }

    public void Report(long count)
    {
        if (!_enabled) return;
        long ms = _sw.ElapsedMilliseconds;
        if (ms - _lastRenderMs < 200) return;
        _lastRenderMs = ms;
        Render(count);
    }

    public void Finish(long count)
    {
        if (!_enabled) return;
        Render(count);
        Console.Error.WriteLine();
    }

    private void Render(long count)
    {
        double secs = Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
        string msg = $"\r  {count:N0} {_noun}  ({count / secs:N0}/s)";
        if (msg.Length < _lastLen) msg += new string(' ', _lastLen - msg.Length);
        _lastLen = msg.Length;
        Console.Error.Write(msg);
    }
}
