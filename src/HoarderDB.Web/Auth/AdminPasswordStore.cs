using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HoarderDB.Web.Auth;

/// <summary>The effective admin password hash and the session "security stamp" that goes with it.</summary>
public sealed record PasswordState(string Hash, string Stamp);

/// <summary>
/// Persists a UI-changed admin password hash as KeyPath/admin-password.json (0600, atomic replace).
/// The store wins over the env hash while its recorded env fingerprint (envFp) still matches the
/// current env hash; an operator who rotates the env hash overrides a stale store. A present but
/// unreadable file aborts startup (never silently falls back to the older env password). Only hashes
/// are stored; nothing in here logs or echoes password material.
/// </summary>
public sealed class AdminPasswordStore(string keyDir)
{
    public const string FileName = "admin-password.json";
    private const int Version = 1;
    private readonly string _path = Path.Combine(keyDir, FileName);

    public string FilePath => _path;

    private sealed record StoreFile(int V, string? Hash, string? EnvFp, string? Stamp);

    public static string Fingerprint(string envHash) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(envHash)));

    /// <summary>Stamp for env-only mode: derived from the hash so an env rotation also ends sessions.</summary>
    public static string DerivedStamp(string hash) => Fingerprint(hash)[..16];

    /// <summary>Loads the effective state. Throws InvalidOperationException (path only, no contents) if the file is corrupt.</summary>
    public PasswordState Load(string envHash, Func<string, bool> looksLikeHash)
    {
        var env = new PasswordState(envHash, DerivedStamp(envHash));
        if (!File.Exists(_path))
        {
            // File.Exists is also false for a dangling symlink or a directory in the way: that is a broken store,
            // not an absent one, so fail startup instead of silently falling back to the older env password.
            if (new FileInfo(_path).LinkTarget is not null || Directory.Exists(_path)) throw Corrupt();
            return env;
        }

        StoreFile? f;
        try
        {
            f = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            throw Corrupt();
        }
        if (f is null || f.V != Version || string.IsNullOrEmpty(f.Hash) || string.IsNullOrEmpty(f.EnvFp)
            || string.IsNullOrEmpty(f.Stamp) || !looksLikeHash(f.Hash))
            throw Corrupt();

        // Operator rotated the env hash: it wins, the stale store is ignored (overwritten on next UI change).
        if (!string.Equals(f.EnvFp, Fingerprint(envHash), StringComparison.OrdinalIgnoreCase)) return env;
        return new PasswordState(f.Hash, f.Stamp);
    }

    private InvalidOperationException Corrupt() => new(
        $"The stored admin password file '{_path}' is unreadable or invalid. " +
        "Fix or delete it to fall back to Auth:PasswordHash (the contents are intentionally not logged).");

    /// <summary>Atomically persists a new hash (tmp file created 0600 in the same dir, then renamed). Returns the new state.</summary>
    public PasswordState Save(string newHash, string envHash)
    {
        var state = new PasswordState(newHash, Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new { v = Version, hash = state.Hash, envFp = Fingerprint(envHash), stamp = state.Stamp });
        var tmp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var opts = new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, opts))
            {
                fs.Write(json);
                fs.Flush(true);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
        return state;
    }
}
