using System.Net;

namespace CrackDB.Web.Tests;

public class ForwardedHeadersTests
{
    // Plain http base address: the redirect scheme then shows whether X-Forwarded-Proto was honored.
    private static async Task<string?> RedirectLocationAsync(CrackDbWebFactory f)
    {
        var c = f.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("http://crackdb.lan"), AllowAutoRedirect = false });
        var req = new HttpRequestMessage(HttpMethod.Get, "/");
        req.Headers.Add("X-Forwarded-Proto", "https");
        req.Headers.Add("X-Forwarded-For", "192.168.50.99");
        var r = await c.SendAsync(req);
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        return r.Headers.Location?.ToString();
    }

    [Fact]
    public async Task Default_IgnoresForwardedProtoFromNonLoopbackPeer()
    {
        using var f = new CrackDbWebFactory(1000, null, "192.168.50.10");
        Assert.StartsWith("http://", await RedirectLocationAsync(f));
    }

    [Fact]
    public async Task ConfiguredProxy_HonorsForwardedProto()
    {
        using var f = new CrackDbWebFactory(1000, new() { ["ForwardedProxies"] = "10.0.0.1, 192.168.50.10" }, "192.168.50.10");
        Assert.StartsWith("https://", await RedirectLocationAsync(f));
    }

    [Fact]
    public async Task ConfiguredNetwork_HonorsForwardedProto()
    {
        using var f = new CrackDbWebFactory(1000, new() { ["ForwardedNetworks"] = "192.168.50.0/24" }, "192.168.50.10");
        Assert.StartsWith("https://", await RedirectLocationAsync(f));
    }

    [Fact]
    public async Task UnlistedPeer_StillIgnoredWhenOthersConfigured()
    {
        using var f = new CrackDbWebFactory(1000, new() { ["ForwardedNetworks"] = "192.168.50.0/24" }, "192.168.60.10");
        Assert.StartsWith("http://", await RedirectLocationAsync(f));
    }

    [Theory]
    [InlineData("ForwardedProxies", "not-an-ip")]
    [InlineData("ForwardedProxies", "192.168.50.10, 300.1.1.1")]
    [InlineData("ForwardedNetworks", "192.168.50.0")]
    [InlineData("ForwardedNetworks", "192.168.50.0/33")]
    [InlineData("ForwardedNetworks", "192.168.50.0/abc")]
    public void MalformedConfig_FailsStartup(string key, string value)
    {
        using var f = new CrackDbWebFactory(1000, new() { [key] = value });
        var ex = Assert.ThrowsAny<Exception>(() => f.CreateClient());
        Assert.Contains("Forwarded", ex.ToString());
    }
}
