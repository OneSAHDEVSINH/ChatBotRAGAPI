using AngleSharp.Html.Parser;
using System.Net;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

public sealed class WebScraperService
{
    private readonly HttpClient _httpClient;
    private readonly HtmlParser _parser = new();

    public WebScraperService(HttpClient httpClient) => _httpClient = httpClient;

    public async Task<string> ScrapeTextAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                $"Target site blocked scraping (403). Try another URL, or use official API/export for: {url}");
        }

        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringAsync(ct);
        var doc = await _parser.ParseDocumentAsync(html, ct);

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

        return string.IsNullOrWhiteSpace(title) ? text : $"{title}\n\n{text}";
    }

    /// <summary>Collapses runs of spaces within lines and blank-line runs to a single paragraph break.</summary>
    private static string NormalizeWhitespace(string text)
    {
        text = Regex.Replace(text, @"[^\S\n]+", " ");          // spaces/tabs/nbsp -> one space (keep \n)
        text = Regex.Replace(text, @" *\n *", "\n");            // trim around line breaks
        text = Regex.Replace(text, @"\n{3,}", "\n\n");          // at most one blank line
        return text.Trim();
    }
}