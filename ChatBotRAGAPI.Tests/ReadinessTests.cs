using System.Net;
using System.Text.Json;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Services.Health;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class ReadinessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(HttpStatusCode Status, JsonElement Body)> ReadyAsync(HttpClient client, Func<JsonElement, bool>? until = null)
    {
        for (var i = 0; ; i++)
        {
            var response = await client.GetAsync("/health/ready", Ct);
            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
            if (until is null || until(body) || i >= 200) return (response.StatusCode, body);
            await Task.Delay(25, Ct);
        }
    }

    private static string Check(JsonElement body, string name) =>
        body.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("name").GetString() == name).GetProperty("status").GetString()!;

    private static string Description(JsonElement body, string name) =>
        body.GetProperty("checks").EnumerateArray().First(c => c.GetProperty("name").GetString() == name).GetProperty("description").GetString()!;

    [Fact]
    public async Task Ready_reports_each_check_once_the_worker_has_started()
    {
        using var app = new RagApiFactory();
        using var client = app.CreateClient();

        var (status, body) = await ReadyAsync(client, b => Check(b, "ingestion-worker") == "Healthy");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Healthy", body.GetProperty("status").GetString());
        Assert.Equal(["vector-store", "ai-backend", "ingestion-worker"],
            body.GetProperty("checks").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Contains("no model backend needed", Description(body, "ai-backend"));
    }

    [Fact]
    public async Task Liveness_stays_cheap_and_ignores_the_backends()
    {
        using var app = OllamaApp(_ => throw new HttpRequestException("connection refused"));
        using var client = app.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await ReadyAsync(client)).Status);
    }

    private static RagApiFactory OllamaApp(Func<HttpRequestMessage, HttpResponseMessage> ollama, Dictionary<string, string>? extra = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Rag:UseLocalAiFallback"] = "false",
            ["Rag:ChatModel"] = "qwen3:8b",
            ["Rag:EmbeddingModel"] = "nomic-embed-text",
        };
        foreach (var (k, v) in extra ?? []) settings[k] = v;
        return new RagApiFactory(settings: settings, services: s => s.AddHttpClient(AiBackendHealthCheck.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler((r, _) => ollama(r))));
    }

    private static HttpResponseMessage Tags(params string[] models) =>
        StubHttpHandler.Json(JsonSerializer.Serialize(new { models = models.Select(m => new { name = m }) }));

    [Fact]
    public async Task Ollama_with_every_model_pulled_is_ready()
    {
        using var app = OllamaApp(_ => Tags("qwen3:8b", "nomic-embed-text:latest")); // ":latest" is implied when no tag is given
        using var client = app.CreateClient();

        var (_, body) = await ReadyAsync(client, b => Check(b, "ingestion-worker") == "Healthy");
        Assert.Equal("Healthy", Check(body, "ai-backend"));
    }

    [Fact]
    public async Task A_model_that_is_not_pulled_makes_the_app_not_ready_and_says_how_to_fix_it()
    {
        using var app = OllamaApp(_ => Tags("qwen3:8b"));
        using var client = app.CreateClient();

        var (status, body) = await ReadyAsync(client);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("Unhealthy", Check(body, "ai-backend"));
        Assert.Contains("ollama pull nomic-embed-text", Description(body, "ai-backend"));
    }

    [Fact]
    public async Task Cloud_providers_are_not_probed()
    {
        var calls = 0;
        using var app = OllamaApp(_ => { calls++; return Tags("nomic-embed-text"); }, new()
        {
            ["Rag:ChatProvider"] = "Anthropic", ["Rag:Anthropic:ApiKey"] = "test-key", ["Rag:ChatModel"] = "claude-opus-5-5",
        });
        using var client = app.CreateClient();

        var (_, body) = await ReadyAsync(client);

        Assert.Equal("Healthy", Check(body, "ai-backend")); // only Ollama's embedding model is checked
        Assert.Contains("Anthropic configured (not probed", Description(body, "ai-backend"));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Qdrant_ping_fails_when_the_collection_is_missing()
    {
        var qdrant = new QdrantClient(new HttpClient(new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound))),
            Options.Create(new RagOptions { QdrantBaseUrl = "http://qdrant.test:6333", QdrantCollection = "knowledge" }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => qdrant.PingAsync(Ct));
        Assert.Contains("'knowledge' answered 404", error.Message);

        var result = await new VectorStoreHealthCheck(qdrant, Options.Create(new RagOptions { UseInMemoryVectorStore = false }))
            .CheckHealthAsync(new HealthCheckContext(), Ct);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task The_worker_is_degraded_until_its_store_is_ready()
    {
        var state = new WorkerState();
        var check = new IngestionWorkerHealthCheck(state);

        Assert.Equal(HealthStatus.Degraded, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);
        state.StoreReady = true;
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(new HealthCheckContext(), Ct)).Status);
    }
}
