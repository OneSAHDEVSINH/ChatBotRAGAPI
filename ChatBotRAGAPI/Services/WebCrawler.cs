using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Breadth-first crawl of one site, starting from a page and following its links. Only pages on the start
/// page's host and under its directory are visited ("https://x.com/docs/intro" covers "/docs/..."; a home page
/// covers the whole site), robots.txt is honoured for discovered pages, and requests are spaced by
/// <see cref="RagOptions.CrawlDelayMilliseconds"/>. Every fetch still goes through the scraper's SSRF guard.
/// </summary>
public sealed class WebCrawler
{
    /// <summary>Links to these are files rather than pages; skipping them saves a request each.</summary>
    private static readonly HashSet<string> NonPageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".jpg", ".jpeg", ".png", ".gif", ".webp", ".svg", ".ico", ".bmp", ".avif",
        ".zip", ".rar", ".7z", ".gz", ".tar", ".exe", ".msi", ".dmg", ".apk",
        ".mp3", ".mp4", ".wav", ".avi", ".mov", ".webm",
        ".css", ".js", ".json", ".xml", ".rss", ".woff", ".woff2", ".ttf", ".eot",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".csv", ".txt",
    };

    private readonly WebScraperService _scraper;
    private readonly RagOptions _options;
    private readonly ILogger<WebCrawler> _logger;

    public WebCrawler(WebScraperService scraper, IOptions<RagOptions> options, ILogger<WebCrawler> logger)
    {
        _scraper = scraper;
        _options = options.Value;
        _logger = logger;
    }

    /// <param name="Ingested">Pages handed to the ingest callback.</param>
    /// <param name="Failed">Discovered pages that could not be fetched (the start page failing fails the crawl).</param>
    /// <param name="Skipped">Fetched but not ingested: not HTML, no text, or a redirect to an already-seen page.</param>
    /// <param name="Complete">Every in-scope link was followed (the crawl did not stop at maxPages).</param>
    /// <param name="Kept">Page keys (<see cref="Key"/>) that are certainly still on the site: ingested, or failed
    /// with an error that may be temporary. Pages that answered 404/410 are not in it.</param>
    /// <param name="InScope">Whether a URL lies on the crawled host and under the crawled path.</param>
    public sealed record CrawlResult(int Ingested, int Failed, int Skipped,
        bool Complete = true, IReadOnlySet<string>? Kept = null, Func<Uri, bool>? InScope = null)
    {
        public override string ToString() =>
            $"Ingested {Ingested} page(s)" + (Failed > 0 ? $", {Failed} failed" : "") + (Skipped > 0 ? $", {Skipped} skipped" : "") + ".";
    }

    /// <param name="startName">Source name for the start page (later pages are named by their URL).</param>
    /// <param name="ingest">Called per page with its source name. Exceptions from it stop the crawl.</param>
    public async Task<CrawlResult> CrawlAsync(
        Uri start, string startName, int maxPages,
        Func<string, ScrapedPage, CancellationToken, Task> ingest, CancellationToken ct)
    {
        // The start page was asked for explicitly: no robots/scope checks, and its errors fail the job.
        var first = await _scraper.ScrapeAsync(start, ct);
        await ingest(startName, first, ct);
        var ingested = 1;
        if (maxPages <= 1) return new CrawlResult(ingested, 0, 0, Complete: false);

        // Scope and robots follow where the start page actually lives (http://x.com may redirect to https://www.x.com).
        var site = first.Url;
        var scope = Directory(site.AbsolutePath);
        var robots = RobotsRules.Parse(await _scraper.GetRobotsTxtAsync(site, ct));

        var seen = new HashSet<string> { Key(start), Key(site) };
        var kept = new HashSet<string> { Key(start), Key(site) };
        var frontier = new Queue<Uri>();
        Enqueue(first.Links);

        int failed = 0, skipped = 0;
        while (frontier.Count > 0 && ingested < maxPages)
        {
            var url = frontier.Dequeue();
            if (_options.CrawlDelayMilliseconds > 0)
                await Task.Delay(_options.CrawlDelayMilliseconds, ct);

            ScrapedPage page;
            try
            {
                page = await _scraper.ScrapeAsync(url, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                _logger.LogWarning("Crawl: skipping {Url}: {Error}", url, ex.Message);
                failed++;
                // Only "not found" / "gone" means the page no longer exists; anything else may be temporary.
                if ((ex as HttpRequestException)?.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone))
                    kept.Add(Key(url));
                continue;
            }

            // A redirect can land on a page already ingested or outside the site.
            var redirected = Key(page.Url) != Key(url);
            if (!page.IsHtml || string.IsNullOrWhiteSpace(page.Text)
                || (redirected && (!seen.Add(Key(page.Url)) || !InScope(page.Url))))
            {
                kept.Add(Key(url)); // it exists; it just isn't a page we index now
                skipped++;
                continue;
            }

            await ingest(page.Url.AbsoluteUri, page, ct);
            ingested++;
            kept.Add(Key(url));
            kept.Add(Key(page.Url));
            Enqueue(page.Links);
        }

        return new CrawlResult(ingested, failed, skipped, Complete: frontier.Count == 0, Kept: kept, InScope: InScope);

        void Enqueue(IEnumerable<Uri> links)
        {
            foreach (var link in links)
            {
                if (InScope(link) && !IsFile(link) && robots.IsAllowed(link.PathAndQuery) && seen.Add(Key(link)))
                    frontier.Enqueue(link);
            }
        }

        bool InScope(Uri link) =>
            string.Equals(link.Host, site.Host, StringComparison.OrdinalIgnoreCase)
            && link.AbsolutePath.StartsWith(scope, StringComparison.Ordinal);
    }

    /// <summary>"/docs/intro" -> "/docs/"; "/docs/" stays; "/" stays.</summary>
    private static string Directory(string path) => path[..(path.LastIndexOf('/') + 1)];

    private static bool IsFile(Uri link) => NonPageExtensions.Contains(Path.GetExtension(link.AbsolutePath));

    /// <summary>Identity of a page for de-duplication: http/https and host case don't make a different page.</summary>
    public static string Key(Uri uri) =>
        uri.Host.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port) + WebScraperService.WithoutFragment(uri).PathAndQuery;
}
