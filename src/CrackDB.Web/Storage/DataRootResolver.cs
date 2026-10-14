using System.Text.RegularExpressions;
using CrackDB.Web.Options;
using Microsoft.Extensions.Options;

namespace CrackDB.Web.Storage;

/// <summary>
/// The only place a db name becomes a filesystem path. Fails closed: name allowlist, canonical
/// containment, symlink-escape check, then a RocksDB marker check so the always-create-if-missing
/// engine constructor is never handed a path that is not already a database (risk R1).
/// </summary>
public sealed partial class DataRootResolver
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NameRegex();

    private readonly string _root;

    public DataRootResolver(IOptions<StorageOptions> options) : this(options.Value.DataRoot) { }

    public DataRootResolver(string dataRoot)
    {
        _root = Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar);
    }

    public string Root => _root;

    /// <summary>Validate a name and return its contained path; does not require existence.</summary>
    public string Resolve(string? name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || !NameRegex().IsMatch(name))
            throw new InvalidDbNameException("Invalid database name.");

        var full = Path.GetFullPath(Path.Combine(_root, name));
        if (full == _root || !full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDbNameException("Invalid database name.");

        // A symlink in the data root must not lead outside it.
        FileSystemInfo? link = null;
        try { if (Directory.Exists(full)) link = Directory.ResolveLinkTarget(full, returnFinalTarget: true); }
        catch (DirectoryNotFoundException) { }   // deleted between Exists and the link check: nothing to follow
        if (link is { } target)
        {
            var realRoot = RealRoot();
            var realTarget = Path.GetFullPath(target.FullName).TrimEnd(Path.DirectorySeparatorChar);
            if (!realTarget.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new InvalidDbNameException("Invalid database name.");
        }
        return full;
    }

    /// <summary>Resolve and require an existing RocksDB directory (has a CURRENT file), else 404.</summary>
    public string ResolveExisting(string? name)
    {
        var full = Resolve(name);
        if (!IsDatabaseDir(full)) throw new DbNotFoundException("Database not found.");
        return full;
    }

    public static bool IsDatabaseDir(string path) =>
        Directory.Exists(path) && File.Exists(Path.Combine(path, "CURRENT"));

    /// <summary>Names of valid, existing databases directly under the root.</summary>
    public IReadOnlyList<string> List()
    {
        var result = new List<string>();
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            try { ResolveExisting(name); result.Add(name); }
            catch (InvalidDbNameException) { }
            catch (DbNotFoundException) { }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private string RealRoot() =>
        Directory.ResolveLinkTarget(_root, returnFinalTarget: true) is { } t
            ? Path.GetFullPath(t.FullName).TrimEnd(Path.DirectorySeparatorChar)
            : _root;
}
