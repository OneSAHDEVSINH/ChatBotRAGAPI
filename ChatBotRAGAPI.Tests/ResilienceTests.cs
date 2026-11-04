using System.Net;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

/// <summary>Exercises the real HttpClientFactory pipeline (as wired in Program.cs) against a stub backend.</summary>
public class ResilienceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Fails the first <paramref name="failures"/> requests with the given status (null = connection error).</summary>
    private static StubHttpHandler FlakyBackend(int failures, HttpStatusCode? failure, string okJson)
    {
        var calls = 0;
        return new StubHttpHandler((_, _) =>
        {
            if (++calls > failures) return StubHttpHandler.Json(okJson);
            if (failure is null) throw new HttpRequestException("No connection could be made (simulated)");
            return StubHttpHandler.Json("""{"error":"busy"}""", failure.Value);
        });
    }

    private static T Build<T>(StubHttpHandler handler, int attempts = 3) where T : class
    {
        var options = new RagOptions
        {
            HttpRetryAttempts = attempts,
            HttpRetryBaseDelaySeconds = 0,
            OllamaBaseUrl = "http://ollama.test",
            QdrantBaseUrl = "http://qdrant.test",
        };
        var services = new ServiceCollection().AddLogging();
        services.AddSingleton(Options.Create(options));
        services.AddHttpClient<T>().AddBackendRetries(options).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider().GetRequiredService<T>();
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(null)] // connection refused: backend restarting
    public async Task Transient_failures_are_retried(HttpStatusCode? failure)
    {
        var handler = FlakyBackend(2, failure, """{"embeddings":[[0.5]]}""");

        var vector = await Build<OllamaClient>(handler).EmbedAsync("hello", Ct);

        Assert.Equal([0.5f], vector);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Single(handler.Requests.Select(r => r.Body).Distinct()); // the same body is re-sent each attempt
    }

    [Fact]
    public async Task Non_transient_errors_fail_immediately()
    {
        var handler = FlakyBackend(5, HttpStatusCode.NotFound, "{}");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Build<OllamaClient>(handler).EmbedAsync("x", Ct));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Gives_up_after_the_configured_attempts()
    {
        var handler = FlakyBackend(100, HttpStatusCode.ServiceUnavailable, "{}");
        await Assert.ThrowsAsync<HttpRequestException>(() => Build<OllamaClient>(handler, attempts: 2).EmbedAsync("x", Ct));
        Assert.Equal(3, handler.Requests.Count); // first try + 2 retries
    }

    [Fact]
    public async Task Zero_attempts_disables_retries()
    {
        var handler = FlakyBackend(1, HttpStatusCode.ServiceUnavailable, """{"embeddings":[[1]]}""");
        await Assert.ThrowsAsync<HttpRequestException>(() => Build<OllamaClient>(handler, attempts: 0).EmbedAsync("x", Ct));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Streaming_generation_is_retried_before_the_stream_starts()
    {
        var handler = FlakyBackend(1, HttpStatusCode.ServiceUnavailable, """{"response":"ok","done":true}""");

        var pieces = new List<string>();
        await foreach (var p in Build<OllamaClient>(handler).StreamAnswerAsync("q", [new SearchHit("t", "s", 1)], [], Ct))
            pieces.Add(p);

        Assert.Equal("ok", string.Concat(pieces));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Unreachable_backend_gives_503_with_reason()
    {
        using var factory = new RagApiFactory(settings: new Dictionary<string, string>
        {
            ["Rag:UseLocalAiFallback"] = "false",
            ["Rag:OllamaBaseUrl"] = "http://127.0.0.1:1", // nothing listens on port 1
            ["Rag:HttpRetryAttempts"] = "0",
        });
        using var client = factory.CreateClient();

        foreach (var url in new[] { "/api/chat/ask", "/api/chat/ask/stream" })
        {
            var response = await client.PostAsync(url,
                System.Net.Http.Json.JsonContent.Create(new AskRequest("hello?")), Ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("backend is unavailable", await response.Content.ReadAsStringAsync(Ct));
        }
    }

    [Fact]
    public async Task Qdrant_writes_are_retried()
    {
        var handler = FlakyBackend(1, null, """{"result":{"status":"completed"}}""");
        var store = Build<QdrantClient>(handler);

        await store.UpsertAsync([new VectorPoint(Guid.NewGuid(), [1f], "t", "s")], Ct);

        Assert.Equal(2, handler.Requests.Count);
    }
}
