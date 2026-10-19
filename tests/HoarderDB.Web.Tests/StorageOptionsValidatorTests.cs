using HoarderDB.Web.Options;
using Xunit;

namespace HoarderDB.Web.Tests;

public class StorageOptionsValidatorTests
{
    private static bool Ok(long mb)
    {
        var dir = Path.GetTempPath();   // exists; only the validator is exercised, no DB is opened
        var o = new StorageOptions { DataRoot = dir, KeyPath = Path.Combine(dir, "keys"), ImportMemoryMb = mb };
        return new StorageOptionsValidator().Validate(null, o).Succeeded;
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(255, false)]
    [InlineData(256, true)]
    [InlineData(768, true)]
    public void ImportMemoryMb_Bounds(long mb, bool ok) => Assert.Equal(ok, Ok(mb));
}
