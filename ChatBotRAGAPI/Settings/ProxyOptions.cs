namespace ChatBotRAGAPI.Settings;

/// <summary>
/// Reverse-proxy support (<c>ForwardedHeaders</c> section). Behind nginx, Apache or a load balancer,
/// every request arrives from the proxy, so per-IP rate limiting would put all clients in one bucket. When enabled,
/// <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> are honoured, but only from the listed proxies (loopback is
/// always trusted), so clients can't spoof their address by sending the header themselves.
/// Hosting in IIS needs none of this: the IIS integration already reports the real client address.
/// </summary>
public sealed class ProxyOptions
{
    public bool Enabled { get; set; } = false;

    /// <summary>Proxy IP addresses allowed to set forwarded headers, e.g. "10.0.0.5".</summary>
    public string[] KnownProxies { get; set; } = [];

    /// <summary>Proxy networks in CIDR notation, e.g. "10.0.0.0/8" (useful when proxy addresses change).</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>How many proxy hops to unwind from X-Forwarded-For.</summary>
    public int ForwardLimit { get; set; } = 1;
}
