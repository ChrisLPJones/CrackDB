using Microsoft.Extensions.Options;
using HoarderDB.Web.Options;

namespace HoarderDB.Web.Storage;

/// <summary>
/// The only place a client-supplied import source becomes a filesystem path. Modeled on
/// <see cref="DataRootResolver"/> and fail-closed: syntax allowlist before any filesystem access,
/// canonical containment, no symlinks below the root (an escaping link is rejected; an in-root link
/// is useless here so it is rejected too), and the target must be an existing non-empty regular
/// file. The empty-file rule also rejects FIFOs and character/block devices, which stat as size 0
/// and would otherwise block or stream forever.
/// </summary>
public sealed class IncomingPathResolver
{
    public const string UploadDirName = ".uploads";
    private const int MaxLength = 1024;

    private readonly string? _root;

    public IncomingPathResolver(IOptions<StorageOptions> options) : this(options.Value.IncomingRoot) { }

    public IncomingPathResolver(string? incomingRoot) =>
        _root = string.IsNullOrWhiteSpace(incomingRoot)
            ? null
            : Path.GetFullPath(incomingRoot).TrimEnd(Path.DirectorySeparatorChar);

    public bool Configured => _root != null;

    public string Root => _root ?? throw new IncomingNotConfiguredException("Server-side import is not configured.");

    public string UploadDir => Path.Combine(Root, UploadDirName);

    /// <summary>Validate a root-relative path and return the contained full path of an existing file.</summary>
    public string Resolve(string? relative)
    {
        var root = Root;
        if (string.IsNullOrEmpty(relative) || relative.Length > MaxLength
            || relative.Contains('\0') || relative.Contains('\\')
            || relative[0] == '/' || Path.IsPathRooted(relative))
            throw new InvalidSourcePathException("Invalid source path.");

        // Segment allowlist: no empty, ".", ".." or dot-prefixed segments (also keeps .uploads unreachable).
        var segments = relative.Split('/');
        foreach (var seg in segments)
            if (seg.Length == 0 || seg[0] == '.' || seg.Any(char.IsControl))
                throw new InvalidSourcePathException("Invalid source path.");

        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidSourcePathException("Invalid source path.");

        // No component below the root may be a symlink (checked per prefix, so a link in the middle
        // of the path is caught, not only a link at the end).
        var cur = root;
        foreach (var seg in segments)
        {
            cur = Path.Combine(cur, seg);
            FileSystemInfo info = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            if (info.LinkTarget != null)
                throw new InvalidSourcePathException("Invalid source path.");
        }

        var fi = new FileInfo(full);
        if (!fi.Exists || Directory.Exists(full) || fi.Length == 0)
            throw new SourceNotFoundException("Source file not found.");
        return full;
    }
}
