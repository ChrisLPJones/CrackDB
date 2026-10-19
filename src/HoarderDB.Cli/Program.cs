using System.Text;
using HoarderDB;

// hoarderdb — import wordlists into a RocksDB store and export/query by length or exact string.

return Cli.Run(args);

static class Cli
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        var command = args[0].ToLowerInvariant();
        var opts = new Options(args[1..]);
        string dbPath = opts.Get("db") ?? "hoarderdb";   // RocksDB is a directory

        try
        {
            switch (command)
            {
                case "import": return Import(dbPath, opts);
                case "export": return Export(dbPath, opts);
                case "lookup": return Lookup(dbPath, opts);
                case "count":  return Count(dbPath, opts);
                case "stats":  return Stats(dbPath, opts);
                case "compact": return Compact(dbPath, opts);
                case "bench":  return Bench(opts);
                case "hashpw": return HashPw();
                default:
                    Console.Error.WriteLine($"Unknown command '{command}'. Run 'hoarderdb --help'.");
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                Console.Error.WriteLine("  caused by: " + inner.Message);
            return 1;
        }
    }

    static int Import(string dbPath, Options opts)
    {
        if (opts.Positional.Count == 0) { Console.Error.WriteLine("import: missing <input-file>."); return 1; }

        // Each argument may be a file, a directory (all files in it), or a
        // wildcard pattern (e.g. *.txt). Everything is gathered into one list.
        var files = ResolveInputFiles(opts.Positional, opts.Get("recursive") != null, out var error);
        if (error != null) { Console.Error.WriteLine("import: " + error); return 1; }
        if (files.Count == 0) { Console.Error.WriteLine("import: no matching files."); return 1; }

        var enc = ResolveEncoding(opts.Get("encoding"));
        var (min, max) = LengthBounds(opts);
        bool showProgress = ProgressEnabled(opts);
        int threads = opts.GetInt("threads") ?? 0;

        long grandTotalBytes = files.Sum(f => new FileInfo(f).Length);
        Console.WriteLine($"Importing {files.Count} file(s), {grandTotalBytes / (1024.0 * 1024 * 1024):N2} GB total -> {dbPath}");

        using var db = new WordlistDatabase(dbPath, enc, bulkLoad: true);
        long before = db.EstimatedCount();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // One progress bar spanning every file: report cumulative bytes across
        // all files, so the percentage and ETA reflect the whole job.
        var bar = new ByteProgressBar(grandTotalBytes, showProgress);
        long bytesFromDoneFiles = 0;
        long totalRead = 0;

        foreach (var input in files)
        {
            using (var fs = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read,
                                           1 << 20, FileOptions.SequentialScan))
            using (var reader = new StreamReader(fs, enc, detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 20))
            {
                long baseBytes = bytesFromDoneFiles;
                totalRead += db.Import(ReadLinesFrom(reader), min, max,
                                       onProgress: lines => bar.Report(baseBytes + fs.Position, totalRead + lines),
                                       threads: threads);
                bytesFromDoneFiles += fs.Length;
            }
        }
        bar.Finish(grandTotalBytes, totalRead);
        sw.Stop();
        long after = db.EstimatedCount();

        double secs = Math.Max(sw.Elapsed.TotalSeconds, 0.001);
        Console.WriteLine($"Imported {files.Count} file(s) -> {dbPath}");
        Console.WriteLine($"  lines read      : {totalRead:N0}");
        Console.WriteLine($"  ~new entries    : {Math.Max(0, after - before):N0}   (approx; duplicates auto-collapsed)");
        Console.WriteLine($"  ~total in db    : {after:N0}   (approx)");
        Console.WriteLine($"  elapsed         : {sw.Elapsed.TotalSeconds:N1}s   ({totalRead / secs:N0} lines/s)");
        return 0;
    }

    // Expands each argument into concrete files: a literal file, every file in a
    // directory (optionally recursive), or a wildcard pattern like "lists/*.txt".
    static List<string> ResolveInputFiles(IEnumerable<string> args, bool recursive, out string? error)
    {
        error = null;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var searchOpt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        void Add(string path)
        {
            var full = Path.GetFullPath(path);
            if (seen.Add(full)) result.Add(full);
        }

        foreach (var arg in args)
        {
            if (Directory.Exists(arg))
            {
                foreach (var f in Directory.EnumerateFiles(arg, "*", searchOpt).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    Add(f);
            }
            else if (arg.Contains('*') || arg.Contains('?'))
            {
                string dir = Path.GetDirectoryName(arg) is { Length: > 0 } d ? d : ".";
                string pattern = Path.GetFileName(arg);
                if (!Directory.Exists(dir)) { error = $"directory not found: {dir}"; return result; }
                foreach (var f in Directory.EnumerateFiles(dir, pattern, searchOpt).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    Add(f);
            }
            else if (File.Exists(arg))
            {
                Add(arg);
            }
            else
            {
                error = $"file not found: {arg}";
                return result;
            }
        }
        return result;
    }

    static int Export(string dbPath, Options opts)
    {
        if (!Directory.Exists(dbPath)) { Console.Error.WriteLine($"export: database not found: {dbPath}"); return 1; }
        string? output = opts.Positional.FirstOrDefault();
        if (output == null) { Console.Error.WriteLine("export: missing <output-file>."); return 1; }

        var (min, max) = LengthBounds(opts);
        long? limit = opts.GetLong("limit");
        var enc = ResolveEncoding(opts.Get("encoding"));

        using var db = new WordlistDatabase(dbPath, enc);
        long written = 0;
        var progress = new CountProgress(ProgressEnabled(opts), "exported");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using (var writer = new StreamWriter(output, false, enc, bufferSize: 1 << 20))
        {
            writer.NewLine = "\n";
            foreach (var word in db.Range(min, max, limit))
            {
                writer.WriteLine(word);
                if ((++written & 0xFFFF) == 0) progress.Report(written);   // every 65536
            }
        }
        progress.Finish(written);
        sw.Stop();

        Console.WriteLine($"Exported -> '{output}'");
        Console.WriteLine($"  criteria   : {DescribeRange(min, max, limit)}");
        Console.WriteLine($"  written    : {written:N0} passwords");
        Console.WriteLine($"  elapsed    : {sw.Elapsed.TotalSeconds:N1}s");
        return 0;
    }

    static int Lookup(string dbPath, Options opts)
    {
        if (!Directory.Exists(dbPath)) { Console.Error.WriteLine($"lookup: database not found: {dbPath}"); return 1; }
        string? word = opts.Positional.FirstOrDefault();
        if (word == null) { Console.Error.WriteLine("lookup: missing <string>."); return 1; }

        var enc = ResolveEncoding(opts.Get("encoding"));
        using var db = new WordlistDatabase(dbPath, enc);
        bool found = db.Contains(word);
        Console.WriteLine(found ? $"FOUND    : '{word}' is in the database." : $"NOT FOUND: '{word}' is not in the database.");
        return found ? 0 : 2;   // exit code 2 = not found (script-friendly)
    }

    static int Count(string dbPath, Options opts)
    {
        if (!Directory.Exists(dbPath)) { Console.Error.WriteLine($"count: database not found: {dbPath}"); return 1; }
        var (min, max) = LengthBounds(opts);
        var enc = ResolveEncoding(opts.Get("encoding"));
        using var db = new WordlistDatabase(dbPath, enc);
        if (min is null && max is null)
        {
            Console.WriteLine($"~{db.EstimatedCount():N0} passwords (approximate total; run with --min/--max for an exact count)");
            return 0;
        }
        long n = db.CountRange(min, max);
        Console.WriteLine($"{n:N0} passwords with {DescribeRange(min, max, null)}");
        return 0;
    }

    static int Stats(string dbPath, Options opts)
    {
        if (!Directory.Exists(dbPath)) { Console.Error.WriteLine($"stats: database not found: {dbPath}"); return 1; }
        var enc = ResolveEncoding(opts.Get("encoding"));
        using var db = new WordlistDatabase(dbPath, enc);
        Console.WriteLine($"Database : {dbPath}");
        Console.WriteLine($"~Total   : {db.EstimatedCount():N0} passwords (approximate)");
        if (opts.Get("full") != null)
        {
            Console.WriteLine();
            Console.WriteLine("Exact length distribution (full scan):");
            Console.WriteLine("  len            count");
            Console.WriteLine("  ---   --------------");
            foreach (var (length, count) in db.LengthDistribution())
                Console.WriteLine($"  {length,3}   {count,14:N0}");
        }
        else
        {
            Console.WriteLine("(add --full for an exact per-length breakdown; it scans the whole database)");
        }
        return 0;
    }

    static int Compact(string dbPath, Options opts)
    {
        if (!Directory.Exists(dbPath)) { Console.Error.WriteLine($"compact: database not found: {dbPath}"); return 1; }
        var enc = ResolveEncoding(opts.Get("encoding"));
        using var db = new WordlistDatabase(dbPath, enc);
        Console.WriteLine($"Compacting {dbPath} (rewrites the whole database; this can take a while)...");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        db.Compact();
        sw.Stop();
        Console.WriteLine($"Done in {sw.Elapsed.TotalSeconds:N1}s. Reads are now optimized.");
        return 0;
    }

    // Prints the PBKDF2 hash the web app expects in CRACKDB_Auth__PasswordHash or HOARDERDB_Auth__PasswordHash. The password is read
    // from stdin only (never argv/env), so it cannot leak into shell history or the process list.
    static int HashPw()
    {
        string? password;
        if (Console.IsInputRedirected)
        {
            password = Console.In.ReadLine()?.TrimEnd('\r');
        }
        else
        {
            password = ReadHidden("Password: ");
            if (password != null && password != ReadHidden("Confirm password: "))
            {
                Console.Error.WriteLine("hashpw: passwords do not match.");
                return 1;
            }
        }
        if (string.IsNullOrEmpty(password)) { Console.Error.WriteLine("hashpw: empty password."); return 1; }
        Console.WriteLine(new Microsoft.AspNetCore.Identity.PasswordHasher<string>().HashPassword("hoarderdb", password));
        return 0;
    }

    static string? ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new StringBuilder();
        ConsoleKeyInfo k;
        while ((k = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    // Isolates the cost of each import stage so we can see where time goes.
    static int Bench(Options opts)
    {
        string? input = opts.Positional.FirstOrDefault();
        if (input == null) { Console.Error.WriteLine("bench: missing <input-file>."); return 1; }
        if (!File.Exists(input)) { Console.Error.WriteLine($"bench: file not found: {input}"); return 1; }
        long capMB = opts.GetLong("mb") ?? 2048;             // how much of the file to test
        long capBytes = capMB * 1024 * 1024;
        var enc = new UTF8Encoding(false);

        Console.WriteLine($"Benchmarking first ~{capMB:N0} MB of '{input}'");

        // Phase 1: raw line reading only (read + UTF-8 decode + string alloc).
        (long lines, long bytes, double secs) p1 = RunPhase(input, enc, capBytes, buildKeys: false);
        Report("1. read lines only          ", p1);

        // Phase 2: read + encode + build the RocksDB key (no DB writes).
        (long lines, long bytes, double secs) p2 = RunPhase(input, enc, capBytes, buildKeys: true);
        Report("2. read + build keys        ", p2);

        Console.WriteLine();
        Console.WriteLine("(Compare these to the real 'import' MB/s. If phase 1 is fast but import is");
        Console.WriteLine(" slow, the cost is DB writes/GC. If phase 2 is much slower than phase 1,");
        Console.WriteLine(" key-building/allocation is the cost.)");
        return 0;

        static (long, long, double) RunPhase(string path, Encoding enc, long capBytes, bool buildKeys)
        {
            long lines = 0; long sink = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            using var reader = new StreamReader(fs, enc, true, 1 << 20);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                lines++;
                if (buildKeys)
                {
                    var word = line.EndsWith('\r') ? line[..^1] : line;
                    if (word.Length == 0) continue;
                    var wb = enc.GetBytes(word);
                    var key = new byte[4 + wb.Length];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(key, word.Length);
                    wb.CopyTo(key, 4);
                    sink += key[0] + key.Length;
                }
                if (fs.Position >= capBytes) break;
            }
            sw.Stop();
            if (sink == long.MaxValue) Console.Write("");   // prevent dead-code elimination
            return (lines, fs.Position, sw.Elapsed.TotalSeconds);
        }

        static void Report(string label, (long lines, long bytes, double secs) r)
        {
            double secs = Math.Max(r.secs, 0.001);
            double mbps = r.bytes / (1024.0 * 1024.0) / secs;
            Console.WriteLine($"  {label}: {mbps,7:0.0} MB/s   {r.lines / secs / 1_000_000.0,5:0.00} M lines/s   ({r.secs,5:0.0}s, {r.lines:N0} lines)");
        }
    }

    // --length N is shorthand for --min N --max N.
    static (int? min, int? max) LengthBounds(Options opts)
    {
        int? exact = opts.GetInt("length");
        if (exact.HasValue) return (exact, exact);
        return (opts.GetInt("min"), opts.GetInt("max"));
    }

    static IEnumerable<string> ReadLinesFrom(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null)
            yield return line;
    }

    // Progress is shown on a TTY by default; suppressed when stderr is redirected
    // (e.g. piped to a file) unless forced with --progress, or off with --no-progress.
    static bool ProgressEnabled(Options opts)
    {
        if (opts.Get("no-progress") != null) return false;
        if (opts.Get("progress") != null) return true;
        return !Console.IsErrorRedirected;
    }

    static Encoding ResolveEncoding(string? name) => (name ?? "utf-8").ToLowerInvariant() switch
    {
        "utf-8" or "utf8"        => new UTF8Encoding(false),
        "latin1" or "iso-8859-1" => Encoding.Latin1,
        "ascii"                  => Encoding.ASCII,
        var other                => Encoding.GetEncoding(other),
    };

    static string DescribeRange(int? min, int? max, long? limit)
    {
        var parts = new List<string>();
        if (min.HasValue && max.HasValue && min == max) parts.Add($"length {min}");
        else if (min.HasValue || max.HasValue)          parts.Add($"length {min?.ToString() ?? "1"}..{max?.ToString() ?? "max"}");
        else                                            parts.Add("all lengths");
        if (limit.HasValue) parts.Add($"limit {limit}");
        return string.Join(", ", parts);
    }

    static void PrintHelp() => Console.WriteLine(
"""
hoarderdb — import wordlists into a database, then export or query by length / exact string.

USAGE
  hoarderdb import <file|dir|pattern>... [--db <dir>] [--min N] [--max N] [--length N]
                               [--recursive] [--threads N] [--encoding enc]
  hoarderdb export <output-file> [--db <dir>] [--min N] [--max N] [--length N] [--limit N] [--encoding enc]
  hoarderdb lookup <string>      [--db <dir>] [--encoding enc]
  hoarderdb count                [--db <dir>] [--min N] [--max N] [--length N]
  hoarderdb stats                [--db <dir>] [--full]
  hoarderdb compact              [--db <dir>]
  hoarderdb hashpw               (reads the password from stdin)

COMMANDS
  import   Add one or more wordlists to the database. Arguments may be files,
           directories (all files inside), or wildcard patterns like "lists/*.txt";
           add --recursive to descend into subdirectories. Duplicates (in-file,
           between files, and against existing data) are collapsed automatically.
           --min/--max/--length limit which lengths get stored.
  export   Write a new wordlist of every password whose length is in range.
  lookup   Check whether one exact password exists (exit code 0 = found, 2 = not).
  count    Count passwords in a length range (exact), or total (approx, instant).
  stats    Show approximate total; --full does an exact per-length breakdown.
  compact  Rewrite the database into few files for fastest reads (run after
           big imports; import defers compaction to stay fast).
  hashpw   Read a password from stdin (hidden prompt on a terminal, or one piped
           line) and print the hash for the web app's CRACKDB_Auth__PasswordHash or HOARDERDB_Auth__PasswordHash.

OPTIONS
  --db <dir>      Database directory (default: hoarderdb).
  --min N         Minimum password length, inclusive.
  --max N         Maximum password length, inclusive.
  --length N      Exact length; shorthand for --min N --max N.
  --limit N       Cap the number of exported passwords.
  --encoding enc  Text encoding: utf-8 (default), latin1, ascii, or a code-page name.
  --threads N     Import writer threads (default: auto). 2-4 is usually plenty.
  --no-progress   Hide the live progress bar (import/export).
  --progress      Force the progress bar even when stderr is redirected.

EXAMPLES
  hoarderdb import huge_500gb.txt              # frequent big imports, auto-deduped
  hoarderdb import a.txt b.txt c.txt           # several files in one command
  hoarderdb import "C:\Wordlists\WPA"          # every file in a folder
  hoarderdb import "lists\*.txt" --recursive   # wildcard, into subfolders too
  hoarderdb import newer.txt                   # merges; overlapping words collapse
  hoarderdb export pw_8to12.txt --min 8 --max 12
  hoarderdb export len8.txt --length 8
  hoarderdb lookup "P@ssw0rd123"
  hoarderdb count --min 8 --max 12
  hoarderdb stats --full

Exports come out ordered by length (shortest first), then alphabetically.
""");
}

/// <summary>Minimal --key value / --flag argument parser.</summary>
sealed class Options
{
    private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();

    public Options(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--"))
            {
                var key = a[2..];
                string val = "true";
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                    val = args[++i];
                _named[key] = val;
            }
            else Positional.Add(a);
        }
    }

    public string? Get(string key) => _named.TryGetValue(key, out var v) ? v : null;

    public int? GetInt(string key) => _named.TryGetValue(key, out var v)
        ? (int.TryParse(v, out var n) ? n : throw new ArgumentException($"--{key} must be an integer, got '{v}'"))
        : null;

    public long? GetLong(string key) => _named.TryGetValue(key, out var v)
        ? (long.TryParse(v, out var n) ? n : throw new ArgumentException($"--{key} must be an integer, got '{v}'"))
        : null;
}
