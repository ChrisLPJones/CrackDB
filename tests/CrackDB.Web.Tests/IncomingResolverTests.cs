using CrackDB.Web.Storage;

namespace CrackDB.Web.Tests;

public class IncomingResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly IncomingPathResolver _r;

    public IncomingResolverTests()
    {
        CrackDbWebFactory.EnsureThrowaway(_root);
        CrackDbWebFactory.EnsureThrowaway(_outside);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_root, "list.txt"), "abc\n");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "deep.txt"), "abc\n");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret\n");
        _r = new IncomingPathResolver(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        Directory.Delete(_outside, true);
    }

    [Fact]
    public void Resolve_AcceptsFilesUnderRoot()
    {
        Assert.Equal(Path.Combine(_root, "list.txt"), _r.Resolve("list.txt"));
        Assert.Equal(Path.Combine(_root, "sub", "deep.txt"), _r.Resolve("sub/deep.txt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("sub/../list.txt")]
    [InlineData("sub/../../x")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\x")]
    [InlineData("C:\\x")]
    [InlineData("a\\b")]
    [InlineData("nul\0byte")]
    [InlineData("sub//deep.txt")]
    [InlineData("./list.txt")]
    [InlineData(".uploads/x")]
    [InlineData(".hidden")]
    public void Resolve_RejectsBadSyntax(string rel) =>
        Assert.Throws<InvalidSourcePathException>(() => _r.Resolve(rel));

    [Fact]
    public void Resolve_NullIsRejected() =>
        Assert.Throws<InvalidSourcePathException>(() => _r.Resolve(null));

    [Fact]
    public void Resolve_MissingFileAndDirectory_AreNotFound()
    {
        Assert.Throws<SourceNotFoundException>(() => _r.Resolve("nope.txt"));
        Assert.Throws<SourceNotFoundException>(() => _r.Resolve("sub"));
    }

    [Fact]
    public void Resolve_EmptyFile_IsRejected()
    {
        File.WriteAllText(Path.Combine(_root, "empty.txt"), "");
        Assert.Throws<SourceNotFoundException>(() => _r.Resolve("empty.txt"));
    }

    [Fact]
    public void Resolve_SymlinkToOutsideFile_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        File.CreateSymbolicLink(Path.Combine(_root, "link.txt"), Path.Combine(_outside, "secret.txt"));
        Assert.Throws<InvalidSourcePathException>(() => _r.Resolve("link.txt"));
    }

    [Fact]
    public void Resolve_SymlinkedDirectoryEscape_IsRejected()
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateSymbolicLink(Path.Combine(_root, "dirlink"), _outside);
        Assert.Throws<InvalidSourcePathException>(() => _r.Resolve("dirlink/secret.txt"));
    }

    [Fact]
    public void Resolve_Fifo_IsRejected()
    {
        if (!OperatingSystem.IsLinux()) return;
        var fifo = Path.Combine(_root, "pipe");
        using (var p = System.Diagnostics.Process.Start("mkfifo", fifo)) { p.WaitForExit(); if (p.ExitCode != 0) return; }
        Assert.Throws<SourceNotFoundException>(() => _r.Resolve("pipe"));
    }

    [Fact]
    public void Unconfigured_Throws() =>
        Assert.Throws<IncomingNotConfiguredException>(() => new IncomingPathResolver("").Resolve("x"));
}
