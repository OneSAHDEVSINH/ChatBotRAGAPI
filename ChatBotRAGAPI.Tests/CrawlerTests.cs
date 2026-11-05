using System.Net;
using System.Net.Http.Json;
using System.Text;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class RobotsRulesTests
{
    [Fact]
    public void Missing_or_empty_robots_allows_everything()
    {
        Assert.True(RobotsRules.Parse(null).IsAllowed("/anything"));
        Assert.True(RobotsRules.Parse("").IsAllowed("/anything"));
        Assert.True(RobotsRules.Parse("User-agent: *\nDisallow:").IsAllowed("/anything")); // empty Disallow = allow
    }

    [Fact]
    public void Disallow_is_a_path_prefix_and_longest_rule_wins()
    {
        var rules = RobotsRules.Parse("""
            User-agent: *
            Disallow: /admin
            Disallow: /private/
            Allow: /private/press
            # comment line
            Disallow: /search  # trailing comment
            """);

        Assert.False(rules.IsAllowed("/admin"));
        Assert.False(rules.IsAllowed("/admin/users"));
        Assert.False(rules.IsAllowed("/private/report"));
        Assert.True(rules.IsAllowed("/private/press/2026"));
        Assert.False(rules.IsAllowed("/search?q=x"));
        Assert.True(rules.IsAllowed("/about"));
    }

    [Fact]
    public void Wildcards_and_end_anchor()
    {
        var rules = RobotsRules.Parse("User-agent: *\nDisallow: /*.pdf$\nDisallow: /*?sessionid=");

        Assert.False(rules.IsAllowed("/files/report.pdf"));
        Assert.False(rules.IsAllowed("/a.pdf/b.pdf"));
        Assert.True(rules.IsAllowed("/files/report.pdf.html"));
        Assert.False(rules.IsAllowed("/page?sessionid=42"));
        Assert.True(rules.IsAllowed("/page?id=42"));
    }

    [Fact]
    public void A_group_for_our_agent_replaces_the_star_group()
    {
        var robots = """
            User-agent: *
            Disallow: /

            User-agent: Googlebot
            User-agent: chatbotragapi
            Disallow: /drafts
            """;

        var rules = RobotsRules.Parse(robots);
        Assert.True(rules.IsAllowed("/about"));   // the "*" group's "Disallow: /" doesn't apply to us
        Assert.False(rules.IsAllowed("/drafts/x"));

        Assert.False(RobotsRules.Parse("User-agent: *\nDisallow: /").IsAllowed("/about"));
    }
}

/// <summary>A fake website: path → HTML, everything else 404. Records which paths were fetched.</summary>
internal sealed class FakeSite
{
    public const string Origin = "https://site.test";
    private readonly Dictionary<string, Func<HttpResponseMessage>> _pages = new();
    public List<string> Fetched { get; } = [];
    public StubHttpHandler Handler { get; }

    public FakeSite() => Handler = new StubHttpHandler((request, _) =>
    {
        var path = request.RequestUri!.PathAndQuery;
        Fetched.Add(path);
        if (!_pages.TryGetValue(path, out var respond)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var response = respond();
        response.RequestMessage ??= request;
        return response;
    });

    public FakeSite Page(string path, string title, string body, params string[] links)
    {
        var anchors = string.Concat(links.Select(l => $"<a href=\"{l}\">link</a>"));
        _pages[path] = () => StubHttpHandler.Html(
            $"<html><head><title>{title}</title></head><body><nav>{anchors}</nav><main><p>{body}</p></main></body></html>");
        return this;
    }

    public FakeSite Raw(string path, Func<HttpResponseMessage> respond)
    {
        _pages[path] = respond;
        return this;
    }

    public FakeSite Robots(string text) =>
        Raw("/robots.txt", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(text) });

    /// <summary>Serves `to`'s response as if `from` had redirected there.</summary>
    public FakeSite Redirect(string from, string to) => Raw(from, () =>
    {
        var response = _pages[to]();
        response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, Origin + to);
        return response;
    });
}

public class WebCrawlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WebCrawler Crawler(FakeSite site) => new(
        new WebScraperService(new HttpClient(site.Handler)),
        Options.Create(new RagOptions { CrawlDelayMilliseconds = 0 }),
        NullLogger<WebCrawler>.Instance);

    private static async Task<(WebCrawler.CrawlResult Result, List<(string Source, string Text)> Pages)> CrawlAsync(
        FakeSite site, string start, int maxPages)
    {
        var pages = new List<(string, string)>();
        var result = await Crawler(site).CrawlAsync(new Uri(FakeSite.Origin + start), FakeSite.Origin + start, maxPages,
            (source, page, _) => { pages.Add((source, page.Text)); return Task.CompletedTask; }, Ct);
        return (result, pages);
    }

    [Fact]
    public async Task One_page_fetches_only_that_page()
    {
        var site = new FakeSite().Page("/", "Home", "Welcome", "/about");

        var (result, pages) = await CrawlAsync(site, "/", 1);

        Assert.Equal(1, result.Ingested);
        Assert.Equal(["/"], site.Fetched); // not even robots.txt
        Assert.Equal(FakeSite.Origin + "/", Assert.Single(pages).Source);
    }

    [Fact]
    public async Task Follows_links_breadth_first_including_navigation()
    {
        var site = new FakeSite()
            .Page("/", "Home", "Welcome", "/about", "/services")
            .Page("/about", "About", "Founded in 2016", "/team")
            .Page("/services", "Services", "Cloud and AI")
            .Page("/team", "Team", "60 people");

        var (result, pages) = await CrawlAsync(site, "/", 10);

        Assert.Equal(4, result.Ingested);
        Assert.Equal(
            ["/", "/about", "/services", "/team"],
            pages.Select(p => new Uri(p.Source).AbsolutePath));
        Assert.Contains(pages, p => p.Text.Contains("Founded in 2016"));
    }

    [Fact]
    public async Task Stops_at_max_pages()
    {
        var site = new FakeSite()
            .Page("/", "Home", "Welcome", "/a", "/b", "/c")
            .Page("/a", "A", "a").Page("/b", "B", "b").Page("/c", "C", "c");

        var (result, _) = await CrawlAsync(site, "/", 2);

        Assert.Equal(2, result.Ingested);
        Assert.DoesNotContain("/b", site.Fetched);
    }

    [Fact]
    public async Task Stays_on_the_host_and_under_the_start_directory()
    {
        var site = new FakeSite()
            .Page("/docs/intro", "Intro", "Start here", "/docs/setup", "/blog/news", "https://other.test/docs/x", "mailto:a@b.c", "#top")
            .Page("/docs/setup", "Setup", "Install it")
            .Page("/blog/news", "News", "Not docs");

        var (result, _) = await CrawlAsync(site, "/docs/intro", 10);

        Assert.Equal(2, result.Ingested);
        Assert.DoesNotContain("/blog/news", site.Fetched);
        Assert.All(site.Handler.Requests, r => Assert.Equal("site.test", r.Request.RequestUri!.Host));
    }

    [Fact]
    public async Task Honours_robots_txt_for_discovered_pages()
    {
        var site = new FakeSite()
            .Robots("User-agent: *\nDisallow: /private")
            .Page("/", "Home", "Welcome", "/private/plans", "/public")
            .Page("/private/plans", "Plans", "Secret")
            .Page("/public", "Public", "Open");

        var (result, _) = await CrawlAsync(site, "/", 10);

        Assert.Equal(2, result.Ingested);
        Assert.DoesNotContain("/private/plans", site.Fetched);
    }

    [Fact]
    public async Task Each_page_is_visited_once_whatever_the_link_spelling()
    {
        var site = new FakeSite()
            .Page("/", "Home", "Welcome", "/about", "/about#team", "https://SITE.test/about", "http://site.test/about", "/")
            .Page("/about", "About", "Us", "/");

        var (result, _) = await CrawlAsync(site, "/", 10);

        Assert.Equal(2, result.Ingested);
        Assert.Single(site.Fetched, p => p == "/about");
    }

    [Fact]
    public async Task Skips_files_non_html_empty_pages_and_redirects_to_seen_pages()
    {
        var site = new FakeSite()
            .Page("/", "Home", "Welcome", "/brochure.pdf", "/feed", "/empty", "/old-home", "/ok")
            .Raw("/feed", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") })
            .Raw("/empty", () => StubHttpHandler.Html("<html><body></body></html>"))
            .Redirect("/old-home", "/")
            .Page("/ok", "Ok", "Fine");

        var (result, pages) = await CrawlAsync(site, "/", 10);

        Assert.DoesNotContain("/brochure.pdf", site.Fetched); // never requested
        Assert.Equal(2, result.Ingested);                     // "/" and "/ok"
        Assert.Equal(3, result.Skipped);                      // feed, empty, old-home
        Assert.Equal(2, pages.Count);
    }

    [Fact]
    public async Task Broken_links_are_counted_but_do_not_stop_the_crawl()
    {
        var site = new FakeSite()
            .Page("/", "Home", "Welcome", "/missing", "/ok")
            .Page("/ok", "Ok", "Fine");

        var (result, _) = await CrawlAsync(site, "/", 10);

        Assert.Equal(new WebCrawler.CrawlResult(2, 1, 0), result);
        Assert.Equal("Ingested 2 page(s), 1 failed.", result.ToString());
    }

    [Fact]
    public async Task A_failing_start_page_fails_the_crawl()
    {
        var site = new FakeSite();
        await Assert.ThrowsAsync<HttpRequestException>(() => CrawlAsync(site, "/", 10));
    }

    [Fact]
    public async Task Scope_follows_a_redirected_start_page()
    {
        var site = new FakeSite()
            .Page("/en/", "Home", "Welcome", "/en/about", "/de/about")
            .Page("/en/about", "About", "Us")
            .Page("/de/about", "Uber", "Uns")
            .Redirect("/", "/en/");

        var (result, pages) = await CrawlAsync(site, "/", 10);

        Assert.Equal(2, result.Ingested);
        Assert.DoesNotContain("/de/about", site.Fetched);
        Assert.Equal(FakeSite.Origin + "/", pages[0].Source); // the start page keeps the requested name
    }
}

public class CrawlApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeSite _site = new FakeSite()
        .Page("/", "Acme", "Acme builds rockets.", "/about", "/contact")
        .Page("/about", "About Acme", "Acme was founded in 1949 in the desert.")
        .Page("/contact", "Contact", "Write to the Acme head office.");

    private readonly RagApiFactory _factory;
    private readonly HttpClient _client;

    public CrawlApiTests()
    {
        _factory = new RagApiFactory(
            settings: new Dictionary<string, string> { ["Rag:CrawlDelayMilliseconds"] = "0", ["Rag:MaxCrawlPages"] = "5" },
            services: s => s.AddHttpClient<WebScraperService>().ConfigurePrimaryHttpMessageHandler(() => _site.Handler));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<HttpResponseMessage> ScrapeAsync(object body) =>
        await _client.PostAsJsonAsync("/api/ingestion/scrape", body, Ct);

    private async Task<JobStatusResponse> WaitForJobAsync(Guid jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{jobId}", Ct);
            if (status!.Status is "Completed" or "Failed") return status;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)]
    public async Task Max_pages_out_of_range_is_rejected(int maxPages)
    {
        var response = await ScrapeAsync(new { url = "https://site.test/", maxPages });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_crawl_stores_each_page_as_a_document_and_answers_from_any_of_them()
    {
        var response = await ScrapeAsync(new { url = "https://site.test/", maxPages = 5 });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!;

        var status = await WaitForJobAsync(job.JobId);
        Assert.Equal("Completed", status.Status);
        Assert.Equal("Ingested 3 page(s).", status.Message);

        var documents = await _client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct);
        Assert.Equal(
            ["https://site.test/", "https://site.test/about", "https://site.test/contact"],
            documents!.Select(d => d.Source).Order());
        Assert.All(documents!, d => Assert.Equal("url", d.SourceType));

        // The founding year is only on the About page, which a single-page scrape of "/" would have missed.
        var answer = await (await _client.PostAsJsonAsync("/api/chat/ask", new AskRequest("When was Acme founded?"), Ct))
            .Content.ReadFromJsonAsync<AskResponse>(Ct);
        Assert.Contains(answer!.Sources, s => s.Source == "https://site.test/about");
        Assert.Contains("1949", answer.Answer);
    }

    [Fact]
    public async Task Without_max_pages_only_the_given_page_is_ingested()
    {
        var job = (await (await ScrapeAsync(new { url = "https://site.test/" })).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!;

        var status = await WaitForJobAsync(job.JobId);
        Assert.Equal("Completed", status.Status);
        Assert.Null(status.Message);

        var documents = await _client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct);
        Assert.Equal("https://site.test/", Assert.Single(documents!).Source);
    }
}
