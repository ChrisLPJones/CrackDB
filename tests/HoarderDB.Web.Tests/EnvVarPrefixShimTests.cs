using Microsoft.Extensions.Configuration;

namespace HoarderDB.Web.Tests;

/// <summary>
/// Tests the exact two-line configuration shim from Program.cs in isolation (a fresh
/// <see cref="ConfigurationBuilder"/>, not the full web host): <c>AddEnvironmentVariables("CRACKDB_")</c>
/// then <c>AddEnvironmentVariables("HOARDERDB_")</c>. A later source wins ties in
/// <see cref="ConfigurationBuilder"/>, so HOARDERDB_ overrides CRACKDB_ when both are set for the same
/// logical setting, while a deployment that only has CRACKDB_ set (the live server's .env file, unchanged)
/// keeps working. Uses process environment variables under a key ("Shim__TestOnlyProbe") nothing else in
/// the app reads, so this can never collide with another test's configuration even if test classes run in
/// parallel; each test restores the variables to unset in a finally block.
/// </summary>
public class EnvVarPrefixShimTests
{
    private const string Key = "Shim__TestOnlyProbe";
    private const string CrackVar = "CRACKDB_" + Key;
    private const string HoarderVar = "HOARDERDB_" + Key;

    private static IConfigurationRoot BuildLikeProgram() =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables("CRACKDB_")
            .AddEnvironmentVariables("HOARDERDB_")
            .Build();

    [Fact]
    public void OnlyCrackdbPrefixSet_IsRead()
    {
        Environment.SetEnvironmentVariable(CrackVar, "from-crackdb");
        try
        {
            var config = BuildLikeProgram();
            Assert.Equal("from-crackdb", config["Shim:TestOnlyProbe"]);
        }
        finally { Environment.SetEnvironmentVariable(CrackVar, null); }
    }

    [Fact]
    public void OnlyHoarderdbPrefixSet_IsRead()
    {
        Environment.SetEnvironmentVariable(HoarderVar, "from-hoarderdb");
        try
        {
            var config = BuildLikeProgram();
            Assert.Equal("from-hoarderdb", config["Shim:TestOnlyProbe"]);
        }
        finally { Environment.SetEnvironmentVariable(HoarderVar, null); }
    }

    [Fact]
    public void BothPrefixesSet_HoarderdbWins()
    {
        Environment.SetEnvironmentVariable(CrackVar, "from-crackdb");
        Environment.SetEnvironmentVariable(HoarderVar, "from-hoarderdb");
        try
        {
            var config = BuildLikeProgram();
            Assert.Equal("from-hoarderdb", config["Shim:TestOnlyProbe"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CrackVar, null);
            Environment.SetEnvironmentVariable(HoarderVar, null);
        }
    }

    [Fact]
    public void NeitherPrefixSet_IsNull()
    {
        var config = BuildLikeProgram();
        Assert.Null(config["Shim:TestOnlyProbe"]);
    }
}
