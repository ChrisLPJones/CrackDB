using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace HoarderDB.Web.Options;

/// <summary>
/// Which peers may supply X-Forwarded-For / X-Forwarded-Proto. Loopback is always trusted; a reverse
/// proxy on another host must be listed explicitly. Values are comma/space/semicolon separated and a
/// malformed value aborts startup (security-relevant: never silently ignored).
/// Env: CRACKDB_ForwardedProxies/HOARDERDB_ForwardedProxies (IP addresses, e.g. "192.168.50.10"),
///      CRACKDB_ForwardedNetworks/HOARDERDB_ForwardedNetworks (CIDRs, e.g. "192.168.50.0/24";
///      HOARDERDB_ wins if both are set).
/// </summary>
public static class ForwardedOptions
{
    public const string ProxiesKey = "ForwardedProxies";
    public const string NetworksKey = "ForwardedNetworks";

    private static readonly char[] Separators = { ',', ' ', ';', '\t', '\n', '\r' };

    public static void Apply(ForwardedHeadersOptions o, IConfiguration config)
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownProxies.Clear();
        o.KnownNetworks.Clear();
        o.KnownProxies.Add(IPAddress.Loopback);
        o.KnownProxies.Add(IPAddress.IPv6Loopback);
        foreach (var ip in ParseProxies(config[ProxiesKey])) o.KnownProxies.Add(ip);
        foreach (var net in ParseNetworks(config[NetworksKey])) o.KnownNetworks.Add(net);
        o.ForwardLimit = 1;
    }

    public static List<IPAddress> ParseProxies(string? value)
    {
        var list = new List<IPAddress>();
        foreach (var t in Split(value))
        {
            if (!IPAddress.TryParse(t, out var ip))
                throw new InvalidOperationException($"{ProxiesKey} (env CRACKDB_{ProxiesKey} or HOARDERDB_{ProxiesKey}) contains an invalid IP address: '{t}'.");
            list.Add(ip);
        }
        return list;
    }

    public static List<Microsoft.AspNetCore.HttpOverrides.IPNetwork> ParseNetworks(string? value)
    {
        var list = new List<Microsoft.AspNetCore.HttpOverrides.IPNetwork>();
        foreach (var t in Split(value))
        {
            var parts = t.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) ||
                !int.TryParse(parts[1], System.Globalization.NumberStyles.None, null, out var prefix) ||
                prefix < 0 || prefix > (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
                throw new InvalidOperationException($"{NetworksKey} (env CRACKDB_{NetworksKey} or HOARDERDB_{NetworksKey}) contains an invalid CIDR: '{t}' (expected e.g. 192.168.50.0/24).");
            list.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(ip, prefix));
        }
        return list;
    }

    private static string[] Split(string? v) =>
        string.IsNullOrWhiteSpace(v) ? [] : v.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
