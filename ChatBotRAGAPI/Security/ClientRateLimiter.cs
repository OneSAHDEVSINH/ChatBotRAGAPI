using ChatBotRAGAPI.Settings;
using System.Threading.RateLimiting;

namespace ChatBotRAGAPI.Security;

public static class ClientRateLimiter
{
    /// <summary>
    /// One fixed-window bucket per client IP, so a single noisy client can't exhaust the limit for everyone.
    /// Behind a reverse proxy every request has the proxy's IP; configure ForwardedHeaders first in that case.
    /// </summary>
    public static PartitionedRateLimiter<HttpContext> Create(RateLimitOptions options) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: PartitionKey(context),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Max(1, options.PermitLimit),
                    Window = TimeSpan.FromSeconds(Math.Max(1, options.WindowSeconds)),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = Math.Max(0, options.QueueLimit)
                }));

    public static string PartitionKey(HttpContext context)
    {
        var ip = context.Connection.RemoteIpAddress;
        if (ip is null) return "unknown";
        return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
    }
}
