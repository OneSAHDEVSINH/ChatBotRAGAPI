using System.Net;
using System.Net.Sockets;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Guards outbound scraping against SSRF: only public http(s) destinations are allowed.
/// The address check runs at connect time (see <see cref="CreateHandler"/>), so it also
/// covers redirects and DNS rebinding, not just the URL the caller submitted.
/// </summary>
public static class UrlSafety
{
    public static bool IsAllowedScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(
                b[0] == 0 ||                                   // 0.0.0.0/8 "this network"
                b[0] == 10 ||                                  // 10.0.0.0/8 private
                (b[0] == 100 && b[1] >= 64 && b[1] <= 127) ||  // 100.64.0.0/10 carrier-grade NAT
                b[0] == 127 ||                                 // 127.0.0.0/8 loopback
                (b[0] == 169 && b[1] == 254) ||                // 169.254.0.0/16 link-local / cloud metadata
                (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||   // 172.16.0.0/12 private
                (b[0] == 192 && b[1] == 0 && b[2] == 0) ||     // 192.0.0.0/24 IETF protocol assignments
                (b[0] == 192 && b[1] == 168) ||                // 192.168.0.0/16 private
                (b[0] == 198 && (b[1] == 18 || b[1] == 19)) || // 198.18.0.0/15 benchmarking
                b[0] >= 224);                                  // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IPv6Loopback.Equals(address) || IPAddress.IPv6None.Equals(address))
                return false;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return false;

            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;           // fc00::/7 unique local
            if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B)
                return false;                                  // 64:ff9b::/96 NAT64 (can reach IPv4 internals)
            return true;
        }

        return false;
    }

    /// <summary>
    /// HTTP handler that resolves the host itself and refuses to connect to non-public addresses.
    /// Proxies are disabled because they would bypass the connect-time check.
    /// </summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, ct);

            var allowed = addresses.Where(IsPublicAddress).ToArray();
            if (allowed.Length == 0)
                throw new HttpRequestException($"Blocked request to non-public address for host '{host}'.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    };
}
