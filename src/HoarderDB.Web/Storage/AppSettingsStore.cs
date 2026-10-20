using System.Text.Json;
using HoarderDB.Web.Options;
using Microsoft.Extensions.Options;

namespace HoarderDB.Web.Storage;

/// <summary>Weekly compaction schedule in server-zone wall time. Day is <see cref="DayOfWeek"/> (0 = Sunday).</summary>
public sealed record CompactionSettings(bool Enabled, int Day, int Hour, int Minute)
{
    public static readonly CompactionSettings Default = new(true, 0, 0, 0);

    /// <summary>Strict allow-list parse: all four fields present, bool + integers in range. Anything else is rejected.</summary>
    public static bool TryParse(JsonElement e, out CompactionSettings? s)
    {
        s = null;
        if (e.ValueKind != JsonValueKind.Object) return false;
        if (!e.TryGetProperty("enabled", out var en) || en.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        if (!Int(e, "day", 0, 6, out var day) || !Int(e, "hour", 0, 23, out var hour) || !Int(e, "minute", 0, 59, out var minute)) return false;
        s = new CompactionSettings(en.GetBoolean(), day, hour, minute);
        return true;
    }

    private static bool Int(JsonElement e, string name, int min, int max, out int v)
    {
        v = 0;
        return e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number
            && p.TryGetInt32(out v) && v >= min && v <= max;
    }
}

/// <summary>
/// Persists operator-editable settings as KeyPath/app-settings.json ({v:1,compaction:{...}}; no secrets).
/// A missing file means defaults. A corrupt/invalid file never aborts startup (unlike the password store):
/// it is moved aside as .corrupt-&lt;utc&gt;, a path-free warning is logged and defaults apply. A symlink or
/// directory at the path is neither read nor written.
/// </summary>
public sealed class AppSettingsStore
{
    public const string FileName = "app-settings.json";
    private const int Version = 1;
    private readonly string _path;

    public AppSettingsStore(IOptions<StorageOptions> storage, ILogger<AppSettingsStore> log)
    {
        var dir = Directory.CreateDirectory(storage.Value.KeyPath);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(dir.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _path = Path.Combine(dir.FullName, FileName);
        Initial = Load(log, out var recovered);
        Recovered = recovered;
    }

    public CompactionSettings Initial { get; }
    /// <summary>The file was unusable at startup and defaults were used.</summary>
    public bool Recovered { get; }

    private CompactionSettings Load(ILogger log, out bool recovered)
    {
        recovered = false;
        if (AtomicJsonFile.IsUnsafe(_path))
        {
            log.LogWarning("The settings file location is not a regular file; using defaults.");
            recovered = true;
            return CompactionSettings.Default;
        }
        if (!File.Exists(_path)) return CompactionSettings.Default;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var ver) && ver == Version
                && root.TryGetProperty("compaction", out var c) && CompactionSettings.TryParse(c, out var s))
                return s!;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { }
        var moved = AtomicJsonFile.MoveAside(_path);
        log.LogWarning("The settings file was invalid{Moved}; using defaults.", moved ? " and was moved aside" : "");
        recovered = true;
        return CompactionSettings.Default;
    }

    /// <summary>Atomically persists the settings. Throws IOException/UnauthorizedAccessException on failure.</summary>
    public void Save(CompactionSettings s) =>
        AtomicJsonFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(
            new { v = Version, compaction = new { enabled = s.Enabled, day = s.Day, hour = s.Hour, minute = s.Minute } }));
}

/// <summary>
/// The live schedule: an immutable snapshot swapped after a successful save, plus a change signal the
/// scheduler awaits. Save order is persist first, then swap memory, then signal.
/// </summary>
public sealed class CompactionSchedule
{
    private readonly AppSettingsStore _store;
    private readonly object _gate = new();
    private volatile CompactionSettings _current;
    private volatile TaskCompletionSource _changed = NewSignal();
    private volatile bool _recovered;

    public CompactionSchedule(AppSettingsStore store)
    {
        _store = store;
        _current = store.Initial;
        _recovered = store.Recovered;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CompactionSettings Current => _current;
    public bool Recovered => _recovered;

    /// <summary>Completes on the next change. Take it BEFORE reading <see cref="Current"/> so a save in between is not lost.</summary>
    public Task Changed => _changed.Task;

    public void Save(CompactionSettings s)
    {
        TaskCompletionSource old;
        lock (_gate)
        {
            _store.Save(s);            // throws: memory stays unchanged
            _current = s;
            _recovered = false;
            old = _changed;
            _changed = NewSignal();
        }
        old.TrySetResult();
    }
}
