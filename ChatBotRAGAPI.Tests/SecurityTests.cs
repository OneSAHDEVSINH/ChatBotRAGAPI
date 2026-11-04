using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using Microsoft.AspNetCore.Http;

namespace ChatBotRAGAPI.Tests;

public class ApiKeyTests : IDisposable
{
    private const string Key = "test-key-123";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RagApiFactory _factory = new(settings: new Dictionary<string, string> { ["Security:ApiKey"] = Key });
    private readonly HttpClient _client;

    public ApiKeyTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static HttpRequestMessage Request(HttpMethod method, string url, string? key, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        if (key is not null) request.Headers.Add(ApiKeyAttribute.HeaderName, key);
        return request;
    }

    public static TheoryData<string, string> ProtectedEndpoints => new()
    {
        { "GET", "/api/documents" },
        { "DELETE", "/api/documents?source=a.txt" },
        { "GET", $"/api/ingestion/{Guid.Empty}" },
        { "POST", "/api/ingestion/scrape" },
    };

    [Theory]
    [MemberData(nameof(ProtectedEndpoints))]
    public async Task Management_endpoints_reject_missing_or_wrong_key(string method, string url)
    {
        foreach (var key in new[] { null, "", "wrong", Key + "x" })
        {
            using var request = Request(new HttpMethod(method), url, key, JsonContent.Create(new ScrapeRequest("https://example.com")));
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(request, Ct)).StatusCode);
        }
    }

    [Fact]
    public async Task Correct_key_is_accepted()
    {
        using var list = Request(HttpMethod.Get, "/api/documents", Key);
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(list, Ct)).StatusCode);

        using var form = new MultipartFormDataContent { { new StringContent("Office hours are 9 to 5."), "file", "hours.txt" } };
        using var upload = Request(HttpMethod.Post, "/api/ingestion/upload", Key, form);
        Assert.Equal(HttpStatusCode.Accepted, (await _client.SendAsync(upload, Ct)).StatusCode);
    }

    [Fact]
    public async Task Rejected_upload_is_not_saved()
    {
        using var form = new MultipartFormDataContent { { new StringContent("x"), "file", "x.txt" } };
        using var upload = Request(HttpMethod.Post, "/api/ingestion/upload", null, form);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(upload, Ct)).StatusCode);
        Assert.False(Directory.Exists(Path.Combine(_factory.DataDirectory, "inbox")) &&
                     Directory.EnumerateFiles(Path.Combine(_factory.DataDirectory, "inbox")).Any());
    }

    [Fact]
    public async Task Chat_and_health_stay_open()
    {
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/chat/ask", new AskRequest("hello?"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/chat/ask/stream", new AskRequest("hello?"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "abd", false)]
    [InlineData("ab", "abc", false)]
    [InlineData("", "abc", false)]
    public void Key_comparison(string provided, string expected, bool match) =>
        Assert.Equal(match, ApiKeyAttribute.Matches(provided, expected));
}

public class RateLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Requests_over_the_limit_get_429()
    {
        using var factory = new RagApiFactory(settings: new Dictionary<string, string>
        {
            ["RateLimit:PermitLimit"] = "3",
            ["RateLimit:WindowSeconds"] = "60",
            ["RateLimit:QueueLimit"] = "0",
        });
        using var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) statuses.Add((await client.GetAsync("/health", Ct)).StatusCode);

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], statuses);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")] // dual-stack sockets: same client, same bucket
    [InlineData("2001:db8::1", "2001:db8::1")]
    [InlineData(null, "unknown")]
    public void Clients_are_partitioned_by_ip(string? ip, string expected)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = ip is null ? null : IPAddress.Parse(ip);
        Assert.Equal(expected, ClientRateLimiter.PartitionKey(context));
    }
}
