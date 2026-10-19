using HoarderDB.Web.Storage;

namespace HoarderDB.Web.Tests;

public class ResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "Test_" + Guid.NewGuid());
    private readonly DataRootResolver _r;

    public ResolverTests()
    {
        HoarderDbWebFactory.EnsureThrowaway(_root);
        HoarderDbWebFactory.EnsureThrowaway(_outside);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_outside);
        _r = new DataRootResolver(_root);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        Directory.Delete(_outside, true);
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("a/b")]
    [InlineData("C:x")]
    [InlineData("-leading")]
    [InlineData(".hidden")]
    [InlineData("nul\0byte")]
    public void Resolve_RejectsBadNames(string name) =>
        Assert.Throws<InvalidDbNameException>(() => _r.Resolve(name));

    [Fact]
    public void Resolve_RejectsOverlongName() =>
        Assert.Throws<InvalidDbNameException>(() => _r.Resolve(new string('a', 65)));

    [Fact]
    public void Resolve_AcceptsGoodNameInsideRoot() =>
        Assert.Equal(Path.Combine(_root, "my-db_1.x"), _r.Resolve("my-db_1.x"));

    [Fact]
    public void ResolveExisting_MissingOrNotRocksDb_Is404()
    {
        Assert.Throws<DbNotFoundException>(() => _r.ResolveExisting("absent"));
        Directory.CreateDirectory(Path.Combine(_root, "empty"));   // no CURRENT file: must not be opened (R1)
        Assert.Throws<DbNotFoundException>(() => _r.ResolveExisting("empty"));
        Assert.Empty(_r.List());
    }

    [Fact]
    public void Resolve_RejectsSymlinkEscapingRoot()
    {
        var link = Path.Combine(_root, "escape");
        try { Directory.CreateSymbolicLink(link, _outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }   // no symlink rights
        File.WriteAllText(Path.Combine(_outside, "CURRENT"), "x");
        Assert.Throws<InvalidDbNameException>(() => _r.Resolve("escape"));
        Assert.Throws<InvalidDbNameException>(() => _r.ResolveExisting("escape"));
        Assert.Empty(_r.List());
    }

    [Fact]
    public void List_ReturnsOnlyRocksDbDirs()
    {
        HoarderDbWebFactory.Seed(Path.Combine(_root, "Test_b"), new[] { "a" });
        HoarderDbWebFactory.Seed(Path.Combine(_root, "Test_a"), new[] { "a" });
        Directory.CreateDirectory(Path.Combine(_root, "plain"));
        Assert.Equal(new[] { "Test_a", "Test_b" }, _r.List());
    }
}
