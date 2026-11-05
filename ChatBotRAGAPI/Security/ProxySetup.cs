using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

namespace ChatBotRAGAPI.Security;

public static class ProxySetup
{
    /// <summary>
    /// Configures forwarded-header handling from <see cref="ProxyOptions"/>. Invalid addresses fail startup
    /// rather than silently trusting nobody (or the wrong proxy).
    /// </summary>
    public static void Configure(ForwardedHeadersOptions options, ProxyOptions proxy)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = Math.Max(1, proxy.ForwardLimit);

        // The defaults (loopback) stay: a proxy on the same machine is the common case.
        foreach (var address in proxy.KnownProxies)
        {
            if (!IPAddress.TryParse(address, out var ip))
                throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains an invalid IP address: '{address}'.");
            options.KnownProxies.Add(ip);
        }

        foreach (var network in proxy.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out var parsed))
                throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains an invalid CIDR network: '{network}'.");
            options.KnownIPNetworks.Add(parsed);
        }
    }
}
