using System.Net;

namespace ChatBotRAGAPI.Tests;

public class ChatUiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly RagApiFactory _factory = new();
    private readonly HttpClient _client;

    public ChatUiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Root_serves_the_chat_page()
    {
        var response = await _client.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        var html = await response.Content.ReadAsStringAsync(Ct);
        Assert.Contains("id=\"composer\"", html);
        Assert.Contains("app.js", html);
    }

    [Theory]
    [InlineData("/app.js", "text/javascript")]
    [InlineData("/app.css", "text/css")]
    [InlineData("/favicon.svg", "image/svg+xml")]
    public async Task Assets_are_served(string path, string mediaType)
    {
        var response = await _client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_page_is_served_with_a_strict_content_security_policy()
    {
        var response = await _client.GetAsync("/", Ct);

        var csp = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'self';", csp); // no other site may frame it unless Widget:AllowedOrigins lists it
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/app.js")]
    [InlineData("/app.css")]
    public async Task Ui_files_are_revalidated_on_every_load(string path)
    {
        var response = await _client.GetAsync(path, Ct);
        Assert.True(response.Headers.CacheControl?.NoCache, $"{path} must be served with Cache-Control: no-cache");
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task The_page_has_no_inline_scripts_or_styles_so_the_policy_does_not_break_it()
    {
        var html = await _client.GetStringAsync("/", Ct);

        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotMatch(@"\sstyle=""", html);
        Assert.DoesNotMatch(@"\son[a-z]+=""", html); // inline event handlers
    }

    [Fact]
    public async Task The_script_never_inserts_server_text_as_html()
    {
        var js = await _client.GetStringAsync("/app.js", Ct);
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("insertAdjacentHTML", js);
        Assert.DoesNotContain("document.write", js);
    }

    [Theory]
    [InlineData("/admin.html", "text/html")]
    [InlineData("/admin.js", "text/javascript")]
    [InlineData("/admin.css", "text/css")]
    public async Task The_admin_page_is_served(string path, string mediaType)
    {
        var response = await _client.GetAsync(path, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("default-src 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
    }

    [Fact]
    public async Task The_admin_page_follows_the_same_csp_and_text_only_rules()
    {
        var html = await _client.GetStringAsync("/admin.html", Ct);
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)", html);
        Assert.DoesNotContain("<style", html);
        Assert.DoesNotMatch(@"\sstyle=""", html);
        Assert.DoesNotMatch(@"\son[a-z]+=""", html);
        foreach (var id in new[] { "key-form", "usage-kpis", "usage-chart", "usage-unanswered", "upload-form", "crawl-form", "schedule-form", "schedules", "jobs", "docs", "feedback", "checks" })
            Assert.Contains($"id=\"{id}\"", html);

        var js = await _client.GetStringAsync("/admin.js", Ct);
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("insertAdjacentHTML", js);
        Assert.DoesNotContain("document.write", js);
        Assert.Contains("X-Api-Key", js);
    }

    [Fact]
    public async Task The_widget_script_is_served_and_only_writes_text()
    {
        var response = await _client.GetAsync("/widget.js", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);

        var js = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("innerHTML", js);
        Assert.Contains("e.origin === botOrigin", js); // only the bot may close the panel
        Assert.Contains("embed=1", js);
    }

    [Fact]
    public async Task By_default_no_site_may_frame_the_chat()
    {
        var csp = Assert.Single((await _client.GetAsync("/", Ct)).Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'self';", csp);
    }

    [Fact]
    public async Task Listed_sites_may_frame_the_chat_page_but_never_the_admin_page()
    {
        using var app = new RagApiFactory(settings: new Dictionary<string, string>
        {
            ["Widget:AllowedOrigins:0"] = "https://www.example.com",
            ["Widget:AllowedOrigins:1"] = "https://shop.example.com:8443/",
        });
        using var client = app.CreateClient();

        var chat = Assert.Single((await client.GetAsync("/?embed=1", Ct)).Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'self' https://www.example.com https://shop.example.com:8443;", chat);

        var admin = Assert.Single((await client.GetAsync("/admin.html", Ct)).Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("frame-ancestors 'none'", admin);
    }

    [Theory]
    [InlineData("www.example.com")]
    [InlineData("https://www.example.com/page")]
    [InlineData("ftp://example.com")]
    public void An_invalid_widget_origin_fails_startup(string origin)
    {
        using var app = new RagApiFactory(settings: new Dictionary<string, string> { ["Widget:AllowedOrigins:0"] = origin });
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("Widget:AllowedOrigins", error.ToString());
    }

    [Fact]
    public async Task Api_routes_are_unaffected()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/no-such-page", Ct)).StatusCode);
    }
}
