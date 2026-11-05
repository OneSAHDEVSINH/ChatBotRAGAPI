using System.Net;
using ChatBotRAGAPI.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Swashbuckle.AspNetCore.Swagger;

namespace ChatBotRAGAPI.Tests;

public class ForwardedHeadersTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The test server has no real connection; give every request this peer address (the proxy, if any).</summary>
    private sealed class PeerAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>A limit of one request per client: a second request from the same client gets 429.</summary>
    private static RagApiFactory Factory(string peer, Dictionary<string, string> settings)
    {
        settings["RateLimit:PermitLimit"] = "1";
        settings["RateLimit:WindowSeconds"] = "60";
        settings["RateLimit:QueueLimit"] = "0";
        return new RagApiFactory(settings: settings,
            services: s => s.AddSingleton<IStartupFilter>(new PeerAddress(IPAddress.Parse(peer))));
    }

    private static async Task<HttpStatusCode> GetAsClientAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request, Ct)).StatusCode;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_each_client_gets_its_own_rate_limit()
    {
        using var factory = Factory("10.0.0.5", new() { ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.2"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsClientAsync(client, "203.0.113.1"));
    }

    [Fact]
    public async Task Known_networks_are_trusted_too()
    {
        using var factory = Factory("10.1.2.3", new() { ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:KnownNetworks:0"] = "10.0.0.0/8" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.2"));
    }

    [Fact]
    public async Task Loopback_proxies_are_trusted_by_default()
    {
        using var factory = Factory("127.0.0.1", new() { ["ForwardedHeaders:Enabled"] = "true" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.2"));
    }

    [Fact]
    public async Task Clients_cannot_spoof_their_address_by_sending_the_header()
    {
        // Forwarding is on, but this peer is not a known proxy: its X-Forwarded-For is ignored.
        using var factory = Factory("198.51.100.9", new() { ["ForwardedHeaders:Enabled"] = "true", ["ForwardedHeaders:KnownProxies:0"] = "10.0.0.5" });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsClientAsync(client, "203.0.113.2"));
    }

    [Fact]
    public async Task Disabled_by_default_so_the_header_is_ignored()
    {
        using var factory = Factory("127.0.0.1", new());
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await GetAsClientAsync(client, "203.0.113.1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, await GetAsClientAsync(client, "203.0.113.2"));
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "10.0.0.0/99")]
    public void Invalid_proxy_settings_fail_startup(string key, string value)
    {
        using var factory = Factory("127.0.0.1", new() { ["ForwardedHeaders:Enabled"] = "true", [key] = value });
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(value, error.ToString());
    }
}

public class SwaggerDocumentTests : IDisposable
{
    private readonly RagApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private Microsoft.OpenApi.OpenApiDocument Document() =>
        _factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

    [Fact]
    public void Ask_endpoints_offer_a_realistic_question_and_a_follow_up_example()
    {
        foreach (var path in new[] { "/api/chat/ask", "/api/chat/ask/stream" })
        {
            var examples = Document().Paths[path].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"].Examples!;

            Assert.Equal(["question", "followUp"], examples.Keys);
            var first = examples["question"].Value!.ToJsonString();
            Assert.Contains("When was the company founded?", first);
            Assert.DoesNotContain("history", first); // a first question has no history
            Assert.DoesNotContain("\"string\"", examples["followUp"].Value!.ToJsonString());
        }
    }

    [Fact]
    public void Scrape_offers_single_page_and_crawl_examples()
    {
        var examples = Document().Paths["/api/ingestion/scrape"].Operations![HttpMethod.Post].RequestBody!.Content!["application/json"].Examples!;
        Assert.Equal(["page", "crawl"], examples.Keys);
        Assert.Contains("maxPages", examples["crawl"].Value!.ToJsonString());
    }

    [Fact]
    public void Endpoints_and_fields_are_described_from_xml_comments()
    {
        var doc = Document();
        Assert.False(string.IsNullOrWhiteSpace(doc.Paths["/api/chat/ask"].Operations![HttpMethod.Post].Summary));
        Assert.Contains("crawls the site", doc.Components!.Schemas![nameof(ScrapeRequest)].Properties!["maxPages"].Description);
        Assert.Contains("503", doc.Paths["/api/chat/ask"].Operations![HttpMethod.Post].Responses!.Keys);
    }
}
