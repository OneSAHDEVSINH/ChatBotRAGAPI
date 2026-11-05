using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using System.Net;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

/// <param name="Url">The address the page was finally served from (after redirects).</param>
/// <param name="IsHtml">False when the server returned something other than HTML (the text is then not meaningful).</param>
/// <param name="Links">Absolute http(s) links on the page, fragments removed, in document order.</param>
public sealed record ScrapedPage(Uri Url, string Text, bool IsHtml, IReadOnlyList<Uri> Links);

public sealed class WebScraperService
{
    private readonly HttpClient _httpClient;
    private readonly HtmlParser _parser = new();

    public WebScraperService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<string> ScrapeTextAsync(string url, CancellationToken ct) =>
        (await ScrapeAsync(new Uri(url), ct)).Text;

    public async Task<ScrapedPage> ScrapeAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"Target site blocked scraping (403). Try another URL, or use official API/export for: {url}");
        }

        response.EnsureSuccessStatusCode();

        var finalUrl = response.RequestMessage?.RequestUri ?? url;
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var isHtml = mediaType is null or "text/html" or "application/xhtml+xml";

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = await _parser.ParseDocumentAsync(html, ct);

        // Links come from the whole page, before navigation and footers are stripped: menus are where most of a
        // site's pages are linked from.
        var links = isHtml ? ExtractLinks(doc, finalUrl) : [];

        foreach (var node in doc.QuerySelectorAll("script,style,nav,footer,header,noscript,svg,form"))
        {
            node.Remove();
        }

        // TextContent glues adjacent blocks together ("Heading</h2><p>Text" -> "HeadingText"), so mark
        // block boundaries with newlines first; the chunker relies on them to split on paragraphs.
        foreach (var el in doc.QuerySelectorAll("br")) el.Replace(doc.CreateTextNode("\n"));
        foreach (var el in doc.QuerySelectorAll("li, tr, dt, dd")) el.After(doc.CreateTextNode("\n"));
        foreach (var el in doc.QuerySelectorAll("td, th")) el.After(doc.CreateTextNode(" "));
        foreach (var el in doc.QuerySelectorAll("p, h1, h2, h3, h4, h5, h6, div, section, article, blockquote, pre, table, ul, ol, dl"))
            el.After(doc.CreateTextNode("\n\n"));

        var primary = doc.QuerySelector("main, article, .content, .main-content, #content");
        var text = NormalizeWhitespace(primary?.TextContent ?? doc.Body?.TextContent ?? string.Empty);
        var title = Regex.Replace(doc.Title ?? string.Empty, @"\s+", " ").Trim();

        return new ScrapedPage(finalUrl, string.IsNullOrWhiteSpace(title) ? text : $"{title}\n\n{text}", isHtml, links);
    }

    private static List<Uri> ExtractLinks(IHtmlDocument doc, Uri pageUrl)
    {
        // <base href> changes what relative links resolve against.
        var baseUrl = Uri.TryCreate(pageUrl, doc.QuerySelector("base[href]")?.GetAttribute("href") ?? "", out var b) ? b : pageUrl;

        var links = new List<Uri>();
        foreach (var a in doc.QuerySelectorAll("a[href]"))
        {
            var href = a.GetAttribute("href")?.Trim();
            if (string.IsNullOrEmpty(href) || href.StartsWith('#')) continue;
            if (!Uri.TryCreate(baseUrl, href, out var link) || !UrlSafety.IsAllowedScheme(link)) continue;

            links.Add(WithoutFragment(link));
        }
        return links;
    }

    public static Uri WithoutFragment(Uri uri) =>
        string.IsNullOrEmpty(uri.Fragment) ? uri : new UriBuilder(uri) { Fragment = "" }.Uri;

    /// <summary>Collapses runs of spaces within lines and blank-line runs to a single paragraph break.</summary>
    private static string NormalizeWhitespace(string text)
    {
        text = Regex.Replace(text, @"[^\S\n]+", " ");          // spaces/tabs/nbsp -> one space (keep \n)
        text = Regex.Replace(text, @" *\n *", "\n");            // trim around line breaks
        text = Regex.Replace(text, @"\n{3,}", "\n\n");          // at most one blank line
        return text.Trim();
    }

    /// <summary>The site's robots.txt, or null when it has none or can't be read (then everything is allowed).</summary>
    public async Task<string?> GetRobotsTxtAsync(Uri site, CancellationToken ct)
    {
        try
        {
            using var response = await _httpClient.GetAsync(new Uri(site, "/robots.txt"), ct);
            return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync(ct) : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
