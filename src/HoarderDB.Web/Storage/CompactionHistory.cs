using System.Text.Json;
using System.Text.RegularExpressions;
using HoarderDB.Web.Options;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Storage;

/// <summary>One finished compact job. RunId is shared by the jobs of one scheduled run; null = started by hand.</summary>
public sealed record CompactionEntry(string? RunId, string Db, string Result, DateTimeOffset StartedAt, DateTimeOffset EndedAt);

public sealed record CompactionRunDb(string Name, string Result, long DurationSeconds);
public sealed record CompactionRun(string Trigger, DateTimeOffset StartedAt, DateTimeOffset EndedAt, long DurationSeconds,
    string Result, IReadOnlyList<CompactionRunDb> Databases);

/// <summary>
/// Advisory history of finished compactions in KeyPath/compaction-history.json ({v:1,entries:[...]},
/// newest last, capped). Separate from the settings file so a failed history write can never touch
/// configuration. Corrupt file: moved aside, history restarts empty. A symlink is neither read nor written.
/// </summary>
public sealed partial class CompactionHistory
{
    public const string FileName = "compaction-history.json";
    public const int Cap = 50;
    private const int Version = 1;
    private static readonly HashSet<string> Results = ["completed", "failed", "cancelled"];

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex RunIdRegex();

    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<CompactionEntry> _entries = [];

    public CompactionHistory(IOptions<StorageOptions> storage, ILogger<CompactionHistory> log)
    {
        var dir = Directory.CreateDirectory(storage.Value.KeyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(dir.FullName, FileName);
        Load(log);
    }

    private void Load(ILogger log)
    {
        if (AtomicJsonFile.IsUnsafe(_path)) { log.LogWarning("The compaction history location is not a regular file; ignoring it."); return; }
        if (!File.Exists(_path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ver) && ver == Version
                && root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() <= Cap)
            {
                var list = new List<CompactionEntry>();
                foreach (var e in arr.EnumerateArray())
                {
                    if (!TryEntry(e, out var entry)) { list = null; break; }
                    list.Add(entry!);
                }
                if (list is not null) { _entries.AddRange(list); return; }
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException) { }
        var moved = AtomicJsonFile.MoveAside(_path);
        log.LogWarning("The compaction history file was invalid{Moved}; starting empty.", moved ? " and was moved aside" : "");
    }

    private static bool TryEntry(JsonElement e, out CompactionEntry? entry)
    {
        entry = null;
        if (e.ValueKind != JsonValueKind.Object) return false;
        string? runId = null;
        if (e.TryGetProperty("runId", out var r) && r.ValueKind != JsonValueKind.Null)
        {
            if (r.ValueKind != JsonValueKind.String || r.GetString() is not { } id || !RunIdRegex().IsMatch(id)) return false;
            runId = id;
        }
        if (!Str(e, "db", out var db) || db.Length is 0 or > 255) return false;
        if (!Str(e, "result", out var result) || !Results.Contains(result)) return false;
        if (!Str(e, "startedAt", out var s) || !DateTimeOffset.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var start)) return false;
        if (!Str(e, "endedAt", out var en) || !DateTimeOffset.TryParse(en, null, System.Globalization.DateTimeStyles.RoundtripKind, out var end)) return false;
        entry = new CompactionEntry(runId, db, result, start, end);
        return true;
    }

    private static bool Str(JsonElement e, string name, out string v)
    {
        v = "";
        if (!e.TryGetProperty(name, out var p) || p.ValueKind != JsonValueKind.String) return false;
        v = p.GetString() ?? "";
        return true;
    }

    /// <summary>Adds an entry (oldest dropped beyond the cap) and persists. Throws on write failure; memory keeps the entry.</summary>
    public void Append(CompactionEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            while (_entries.Count > Cap) _entries.RemoveAt(0);
            var json = JsonSerializer.SerializeToUtf8Bytes(new
            {
                v = Version,
                entries = _entries.Select(x => new { runId = x.RunId, db = x.Db, result = x.Result, startedAt = x.StartedAt, endedAt = x.EndedAt }),
            });
            AtomicJsonFile.Write(_path, json);
        }
    }

    /// <summary>The most recent run: all entries sharing the newest entry's RunId (a scheduled run), or the single manual entry.</summary>
    public CompactionRun? LastRun()
    {
        List<CompactionEntry> group;
        lock (_gate)
        {
            if (_entries.Count == 0) return null;
            var last = _entries[^1];
            group = last.RunId is null ? [last] : _entries.Where(x => x.RunId == last.RunId).ToList();
        }
        var started = group.Min(x => x.StartedAt);
        var ended = group.Max(x => x.EndedAt);
        var results = group.Select(x => x.Result).Distinct().ToList();
        return new CompactionRun(
            group[0].RunId is null ? "manual" : "scheduled", started, ended,
            Secs(started, ended), results.Count == 1 ? results[0] : "partial",
            group.Select(x => new CompactionRunDb(x.Db, x.Result, Secs(x.StartedAt, x.EndedAt))).ToList());
    }

    private static long Secs(DateTimeOffset a, DateTimeOffset b) => Math.Max(0, (long)(b - a).TotalSeconds);
}
