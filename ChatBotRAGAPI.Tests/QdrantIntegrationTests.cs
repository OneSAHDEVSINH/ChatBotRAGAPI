using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Testcontainers.Qdrant;

namespace ChatBotRAGAPI.Tests;

/// <summary>
/// One real Qdrant in Docker for the whole class. Without Docker the tests skip (a developer machine), unless
/// REQUIRE_DOCKER=true (CI), where a missing Docker must fail rather than silently skip.
/// </summary>
public sealed class QdrantFixture : IAsyncLifetime
{
    private QdrantContainer? _container;

    public string? Url { get; private set; }
    public string? Unavailable { get; private set; }

    public async ValueTask InitializeAsync()
    {
        try
        {
            _container = new QdrantBuilder("qdrant/qdrant:v1.13.4").Build();
            await _container.StartAsync();
            Url = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(6333)}";
        }
        catch (Exception ex)
        {
            Unavailable = ex.Message;
            if (Environment.GetEnvironmentVariable("REQUIRE_DOCKER") == "true")
                throw new InvalidOperationException("REQUIRE_DOCKER is set but Qdrant could not be started in Docker.", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }

    public void SkipWithoutDocker() =>
        Assert.SkipWhen(Url is null, $"Docker is not available, so the real-Qdrant tests are skipped ({Unavailable})");
}

public sealed class QdrantIntegrationTests(QdrantFixture qdrant) : IClassFixture<QdrantFixture>, IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _collection = "test_" + Guid.NewGuid().ToString("N");
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ragqdrant-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private QdrantClient Client(int dimensions = 4) => new(new HttpClient(),
        Options.Create(new RagOptions { QdrantBaseUrl = qdrant.Url!, QdrantCollection = _collection, EmbeddingDimensions = dimensions }));

    private static VectorPoint Point(int n, float[] vector, string text) =>
        new(new Guid(n, 0, 0, new byte[8]), vector, text, "doc.txt");

    [Fact]
    public async Task Upsert_search_and_delete_against_a_real_server()
    {
        qdrant.SkipWithoutDocker();
        var store = Client();
        await store.EnsureCollectionAsync(Ct);
        await store.EnsureCollectionAsync(Ct); // idempotent

        await store.UpsertAsync([Point(1, [1, 0, 0, 0], "east"), Point(2, [0, 1, 0, 0], "north"), Point(3, [0.9f, 0.1f, 0, 0], "east-ish")], Ct);

        var hits = await store.SearchAsync([1, 0, 0, 0], 2, Ct);
        Assert.Equal(["east", "east-ish"], hits.Select(h => h.Text));
        Assert.Equal("doc.txt", hits[0].Source);
        Assert.Equal(1.0, hits[0].Score, 3);

        await store.UpsertAsync([Point(1, [0, 0, 1, 0], "moved")], Ct); // same id replaces the point
        await store.DeleteAsync([Point(3, [0, 0, 0, 0], "").Id], Ct);
        hits = await store.SearchAsync([1, 0, 0, 0], 3, Ct);
        Assert.DoesNotContain(hits, h => h.Text is "east" or "east-ish");
        Assert.Contains(hits, h => h.Text == "moved");
    }

    [Fact]
    public async Task A_collection_of_another_vector_size_is_refused()
    {
        qdrant.SkipWithoutDocker();
        await Client(dimensions: 4).EnsureCollectionAsync(Ct);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Client(dimensions: 8).EnsureCollectionAsync(Ct));
        Assert.Contains("8", error.Message);
    }

    private RagApiFactory App(CountingEmbedder embedder) => new(_dataDir, new Dictionary<string, string>
    {
        ["Rag:UseInMemoryVectorStore"] = "false",
        ["Rag:QdrantBaseUrl"] = qdrant.Url!,
        ["Rag:QdrantCollection"] = _collection,
    }, s => s.AddSingleton<IEmbeddingClient>(embedder));

    [Fact]
    public async Task Documents_survive_a_restart_without_reindexing()
    {
        qdrant.SkipWithoutDocker();
        using (var app = App(new CountingEmbedder()))
        using (var client = app.CreateClient())
        {
            using var form = new MultipartFormDataContent { { new StringContent("Refund policy: a full refund within 30 days of purchase."), "file", "handbook.txt" } };
            var job = (await (await client.PostAsync("/api/ingestion/upload", form, Ct)).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!;
            for (var i = 0; i < 400; i++)
            {
                var s = await client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{job.JobId}", Ct);
                if (s!.Status is "Completed" or "Failed") { Assert.Equal("Completed", s.Status); break; }
                await Task.Delay(25, Ct);
            }
        }

        // A persistent store needs no reindex: the vectors are in Qdrant, and the keyword index is rebuilt from text.
        var restartedEmbedder = new CountingEmbedder();
        using var restarted = App(restartedEmbedder);
        using var c2 = restarted.CreateClient();
        AskResponse? answer = null;
        for (var i = 0; i < 200 && (answer is null || answer.Sources.Count == 0); i++)
        {
            if (i > 0) await Task.Delay(25, Ct);
            answer = await (await c2.PostAsJsonAsync("/api/chat/ask", new AskRequest("What is the refund policy?"), Ct)).Content.ReadFromJsonAsync<AskResponse>(Ct);
        }

        Assert.Equal("handbook.txt", answer!.Sources[0].Source);
        Assert.Equal("both", answer.Sources[0].Match); // found by vector (Qdrant) and keyword (rebuilt) search
        Assert.Equal(0, restartedEmbedder.ChunksEmbedded);
    }
}
