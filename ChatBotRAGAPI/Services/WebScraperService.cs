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

        var primary = doc.QuerySelector("main, article, .content, .main-content, #content");
        var text = primary?.TextContent ?? doc.Body?.TextContent ?? string.Empty;

        text = Regex.Replace(text, @"\s+", " ").Trim();
        var title = Regex.Replace(doc.Title ?? string.Empty, @"\s+", " ").Trim();

        return string.IsNullOrWhiteSpace(title) ? text : $"{title}\n{text}";
        //return $"{doc.Title}\n{doc.Body?.TextContent}".Trim();
    }
}