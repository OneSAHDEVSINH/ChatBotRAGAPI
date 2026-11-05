using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class KeywordIndexTests
{
    private static Guid Id(int n) => new(n, 0, 0, new byte[8]);

    [Fact]
    public void Rare_exact_terms_outrank_common_ones()
    {
        var index = new KeywordIndex();
        index.Upsert(Id(1), "Revenue grew in every quarter of the year.", "a");
        index.Upsert(Id(2), "Quarterly revenue report for the board.", "b");
        index.Upsert(Id(3), "Q3-2026 revenue was 214000 USD.", "c");

        var hits = index.Search("revenue Q3 2026", 3);

        Assert.Equal(Id(3), hits[0].Id);
        Assert.Equal(1.0, hits[0].Coverage);
        Assert.All(hits.Skip(1), h => Assert.True(h.Score < hits[0].Score));
    }

    [Fact]
    public void Coverage_is_the_share_of_query_terms_present()
    {
        var index = new KeywordIndex();
        index.Upsert(Id(1), "The Pro plan costs 999 per month.", "s");

        var hit = Assert.Single(index.Search("What does the Enterprise plan cost?", 5));
        // Terms: enterprise, plan, cost. "plan" and "cost" match ("costs" stems to "cost"); "enterprise" does not.
        Assert.Equal(2.0 / 3, hit.Coverage, 6);
    }

    [Fact]
    public void Stopword_only_queries_and_empty_index_return_nothing()
    {
        var index = new KeywordIndex();
        Assert.Empty(index.Search("anything", 5));
        index.Upsert(Id(1), "The office is in Berlin.", "s");
        Assert.Empty(index.Search("what is the", 5));
    }

    [Fact]
    public void Upsert_replaces_and_remove_forgets()
    {
        var index = new KeywordIndex();
        index.Upsert(Id(1), "apples and pears", "s");
        index.Upsert(Id(1), "oranges only", "s");

        Assert.Empty(index.Search("apples", 5));
        Assert.Single(index.Search("oranges", 5));

        index.Remove([Id(1), Id(99)]);
        Assert.Empty(index.Search("oranges", 5));
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public async Task Decorator_mirrors_successful_writes_only()
    {
        var keywords = new KeywordIndex();
        var store = new KeywordIndexingVectorStore(new InMemoryVectorStore(), keywords);
        var ct = TestContext.Current.CancellationToken;

        await store.UpsertAsync([new VectorPoint(Id(1), [1f], "warranty two years", "w.txt")], ct);
        Assert.Equal("w.txt", Assert.Single(keywords.Search("warranty", 5)).Source);

        await store.DeleteAsync([Id(1)], ct);
        Assert.Empty(keywords.Search("warranty", 5));

        var failing = new KeywordIndexingVectorStore(new ThrowingStore(), keywords);
        await Assert.ThrowsAsync<HttpRequestException>(() =>
            failing.UpsertAsync([new VectorPoint(Id(2), [1f], "orphan text", "x")], ct));
        Assert.Empty(keywords.Search("orphan", 5));
    }

    private sealed class ThrowingStore : IVectorStore
    {
        public bool IsPersistent => true;
        public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => throw new HttpRequestException("down");
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => throw new HttpRequestException("down");
        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct) => throw new HttpRequestException("down");
    }
}

public class HybridRetrievalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FixedEmbedder : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(new float[] { 1 });
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1 }).ToList());
    }

    private sealed class FixedStore(params SearchHit[] hits) : IVectorStore
    {
        public bool IsPersistent => false;
        public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>(hits.Take(topK).ToList());
    }

    private sealed class EchoGenerator : IAnswerGenerator
    {
        public Task<string> GenerateAnswerAsync(string q, IReadOnlyList<SearchHit> c, IReadOnlyList<ChatMessage> h, CancellationToken ct) =>
            Task.FromResult(string.Join("|", c.Select(x => x.Text)));
        public IAsyncEnumerable<string> StreamAnswerAsync(string q, IReadOnlyList<SearchHit> c, IReadOnlyList<ChatMessage> h, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private const string Kpi = "Q3-2026 | 605 | 271 | 47 | 214000";
    private const string Profile = "Giriraj Digital was founded in 2016 in Ahmedabad.";
    private const string Catalog = "Umbraco Managed Support, Pro, 999 USD per month.";

    private static RagService Service(IVectorStore store, KeywordIndex keywords, bool hybrid = true) =>
        new(new FixedEmbedder(), new EchoGenerator(), store,
            Options.Create(new RagOptions { UseLocalAiFallback = false, MinScore = 0.45, TopK = 3, HybridSearch = hybrid, MinKeywordCoverage = 0.5 }),
            keywords);

    private static KeywordIndex Index(params (string Text, string Source)[] chunks)
    {
        var index = new KeywordIndex();
        for (var i = 0; i < chunks.Length; i++) index.Upsert(Guid.NewGuid(), chunks[i].Text, chunks[i].Source);
        return index;
    }

    [Fact]
    public async Task Exact_term_below_min_score_is_found_by_keyword()
    {
        // The embedding ranks the KPI row low (0.30 < MinScore); vector-only retrieval would answer "no data".
        var store = new FixedStore(new SearchHit(Profile, "profile.txt", 0.40), new SearchHit(Kpi, "kpi.xlsx", 0.30));
        var keywords = Index((Kpi, "kpi.xlsx"), (Profile, "profile.txt"));

        Assert.Equal(RagService.NoDataAnswer, (await Service(store, keywords, hybrid: false).AskAsync("Q3-2026 deals won", null, Ct)).Answer);

        var result = await Service(store, keywords).AskAsync("Q3-2026 deals won", null, Ct);
        var source = Assert.Single(result.Sources);
        Assert.Equal(("kpi.xlsx", "keyword", (double?)0.3), (source.Source, source.Match, source.Score));
    }

    [Fact]
    public async Task Keyword_only_passage_has_no_score()
    {
        var keywords = Index((Kpi, "kpi.xlsx"));
        var result = await Service(new FixedStore(), keywords).AskAsync("Q3 2026", null, Ct);
        var source = Assert.Single(result.Sources);
        Assert.Equal(("keyword", (double?)null), (source.Match, source.Score));
    }

    [Fact]
    public async Task Low_keyword_coverage_is_not_enough()
    {
        // Shares only "2016" out of four terms: not admitted, and the weak vector hit isn't either.
        var store = new FixedStore(new SearchHit(Profile, "profile.txt", 0.2));
        var keywords = Index((Profile, "profile.txt"));
        var result = await Service(store, keywords).AskAsync("Olympic medal table 2016", null, Ct);
        Assert.Equal(RagService.NoDataAnswer, result.Answer);
    }

    [Fact]
    public async Task Sharing_generic_words_is_not_a_match_with_default_settings()
    {
        // Real case from the eval: 3 of 4 terms matched a compiler paper; "guitar" appears nowhere.
        const string paper = "The best way to learn how a compiler works is to write one.";
        var store = new FixedStore(new SearchHit(paper, "paper.pdf", 0.39));
        var keywords = Index((paper, "paper.pdf"));
        var service = new RagService(new FixedEmbedder(), new EchoGenerator(), store,
            Options.Create(new RagOptions { UseLocalAiFallback = false }), keywords);

        var result = await service.AskAsync("What is the best way to learn the guitar?", null, Ct);

        Assert.Equal(RagService.NoDataAnswer, result.Answer);
    }

    [Fact]
    public async Task Terse_exact_query_is_admitted_with_default_settings()
    {
        const string story = "Added Redis caching for product pages.";
        var store = new FixedStore(new SearchHit(story, "story.docx", 0.4494)); // real eval score, just under MinScore
        var service = new RagService(new FixedEmbedder(), new EchoGenerator(), store,
            Options.Create(new RagOptions { UseLocalAiFallback = false }), Index((story, "story.docx")));

        var source = Assert.Single((await service.AskAsync("Redis caching", null, Ct)).Sources);

        Assert.Equal(("story.docx", "keyword"), (source.Source, source.Match));
    }

    [Fact]
    public async Task Passages_found_both_ways_rank_first()
    {
        var store = new FixedStore(
            new SearchHit(Profile, "profile.txt", 0.70),
            new SearchHit(Catalog, "catalog.csv", 0.60));
        var keywords = Index((Catalog, "catalog.csv"), (Profile, "profile.txt"));

        var result = await Service(store, keywords).AskAsync("Pro plan Umbraco support price", null, Ct);

        Assert.Equal(["catalog.csv", "profile.txt"], result.Sources.Select(s => s.Source));
        Assert.Equal(["both", "semantic"], result.Sources.Select(s => s.Match));
        Assert.Equal(1, result.Sources[0].Id);
    }

    [Fact]
    public async Task Hybrid_off_is_plain_vector_search()
    {
        var store = new FixedStore(new SearchHit(Profile, "profile.txt", 0.70), new SearchHit(Kpi, "kpi.xlsx", 0.30));
        var result = await Service(store, Index((Kpi, "kpi.xlsx")), hybrid: false).AskAsync("Q3-2026", null, Ct);
        Assert.Equal(("profile.txt", "semantic"), (result.Sources.Single().Source, result.Sources.Single().Match));
    }
}

public class KeywordIndexRebuildTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Stands in for Qdrant: survives an app restart because the same instance is reused.</summary>
    private sealed class PersistentStore : IVectorStore
    {
        private readonly InMemoryVectorStore _inner = new();
        public bool IsPersistent => true;
        public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => _inner.UpsertAsync(points, ct);
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => _inner.DeleteAsync(ids, ct);
        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct) => _inner.SearchAsync(vector, topK, ct);
    }

    [Fact]
    public async Task Keyword_index_is_rebuilt_from_stored_documents_when_the_store_is_persistent()
    {
        var store = new PersistentStore();
        void UseStore(IServiceCollection services) =>
            services.AddSingleton<IVectorStore>(sp => new KeywordIndexingVectorStore(store, sp.GetRequiredService<KeywordIndex>()));

        using var first = new RagApiFactory(services: UseStore);
        using (var client = first.CreateClient())
        {
            using var form = new MultipartFormDataContent { { new StringContent("Warranty: two years on all devices."), "file", "w.txt" } };
            var job = (await (await client.PostAsync("/api/ingestion/upload", form, Ct)).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
            for (var i = 0; i < 100 && (await client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{job}", Ct))!.Status != "Completed"; i++)
                await Task.Delay(50, Ct);
        }

        using var restarted = new RagApiFactory(first.DataDirectory, services: UseStore);
        using var _ = restarted.CreateClient(); // starts the host (and its worker)
        var keywords = restarted.Services.GetRequiredService<KeywordIndex>();

        for (var i = 0; i < 100 && keywords.Count == 0; i++) await Task.Delay(50, Ct);
        Assert.Equal("w.txt", Assert.Single(keywords.Search("warranty", 5)).Source);
    }
}
