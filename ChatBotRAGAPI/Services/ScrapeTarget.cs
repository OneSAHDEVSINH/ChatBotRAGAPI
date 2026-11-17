namespace ChatBotRAGAPI.Services;

/// <summary>The rules for a scrape or crawl request, shared by the scrape endpoint and crawl schedules.</summary>
public static class ScrapeTarget
{
    /// <returns>Why the request is not acceptable, or null (then <paramref name="uri"/> is the parsed address).</returns>
    public static string? Validate(string? url, int maxPages, bool removeMissing, int maxCrawlPages, out Uri? uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || !UrlSafety.IsAllowedScheme(uri))
            return "Invalid URL. Only absolute http/https URLs are supported.";

        // Fast rejection for literal private IPs; hostnames are checked at connect time by the scraper's handler.
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !UrlSafety.IsPublicAddress(ip))
            return "URLs pointing to private, loopback or link-local addresses are not allowed.";

        if (maxPages < 1 || maxPages > maxCrawlPages)
            return $"maxPages must be between 1 and {maxCrawlPages}.";
        if (removeMissing && maxPages == 1)
            return "removeMissing needs a crawl: set maxPages above 1.";

        return null;
    }
}
