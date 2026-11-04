using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class InMemoryVectorStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upsert_with_same_id_replaces_instead_of_duplicating()
    {
        var store = new InMemoryVectorStore();
        var id = Guid.NewGuid();
        await store.UpsertAsync([new VectorPoint(id, [1, 0], "old", "a.txt")], Ct);
        await store.UpsertAsync([new VectorPoint(id, [1, 0], "new", "b.txt")], Ct);

        var hits = await store.SearchAsync([1, 0], 10, Ct);
        Assert.Equal(new SearchHit("new", "b.txt", 1.0), Assert.Single(hits));
    }

    [Fact]
    public async Task Search_ranks_by_cosine_and_respects_topK()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
        [
            new VectorPoint(Guid.NewGuid(), [0, 1], "orthogonal", "s"),
            new VectorPoint(Guid.NewGuid(), [1, 0], "exact", "s"),
            new VectorPoint(Guid.NewGuid(), [1, 1], "diagonal", "s"),
        ], Ct);

        var hits = await store.SearchAsync([2, 0], 2, Ct);
        Assert.Equal(["exact", "diagonal"], hits.Select(h => h.Text));
        Assert.Equal(1.0, hits[0].Score, 6);
        Assert.Equal(Math.Sqrt(0.5), hits[1].Score, 6);
    }

    [Fact]
    public async Task Delete_removes_points_and_ignores_unknown_ids()
    {
        var store = new InMemoryVectorStore();
        var keep = Guid.NewGuid();
        var drop = Guid.NewGuid();
        await store.UpsertAsync([new VectorPoint(keep, [1, 0], "keep", "s"), new VectorPoint(drop, [1, 0], "drop", "s")], Ct);

        await store.DeleteAsync([drop, Guid.NewGuid()], Ct);

        Assert.Equal("keep", Assert.Single(await store.SearchAsync([1, 0], 10, Ct)).Text);
    }

    [Fact]
    public async Task Zero_vector_scores_zero()
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync([new VectorPoint(Guid.NewGuid(), [1, 0], "t", "s")], Ct);
        Assert.Equal(0, (await store.SearchAsync([0, 0], 1, Ct))[0].Score);
    }
}

public class LocalEmbeddingClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static LocalEmbeddingClient Client(int dims = 768) => new(Options.Create(new RagOptions { EmbeddingDimensions = dims }));

    [Fact]
    public async Task Vectors_are_unit_length_with_configured_dimensions()
    {
        var v = await Client(128).EmbedAsync("The quick brown fox", Ct);
        Assert.Equal(128, v.Length);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => (double)x * x)), 5);
    }

    [Fact]
    public async Task Dimensions_have_a_floor_of_64() =>
        Assert.Equal(64, (await Client(8).EmbedAsync("x", Ct)).Length);

    [Fact]
    public async Task Embedding_is_deterministic_and_case_insensitive()
    {
        var client = Client();
        Assert.Equal(await client.EmbedAsync("Hello World", Ct), await client.EmbedAsync("hello world", Ct));
    }

    [Fact]
    public async Task Blank_text_gives_zero_vector() =>
        Assert.All(await Client().EmbedAsync("  ", Ct), x => Assert.Equal(0f, x));

    [Fact]
    public async Task Stopwords_are_ignored()
    {
        var client = Client();
        Assert.Equal(await client.EmbedAsync("refund policy", Ct), await client.EmbedAsync("What is the refund policy?", Ct));
        Assert.All(await client.EmbedAsync("what is the", Ct), x => Assert.Equal(0f, x));
    }

    [Fact]
    public async Task Batch_matches_single_embeddings()
    {
        var client = Client();
        var batch = await client.EmbedBatchAsync(["alpha beta", "gamma"], Ct);
        Assert.Equal(await client.EmbedAsync("alpha beta", Ct), batch[0]);
        Assert.Equal(await client.EmbedAsync("gamma", Ct), batch[1]);
    }
}

public class LocalAnswerGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly LocalAnswerGenerator _generator = new();

    [Fact]
    public async Task No_shared_terms_gives_no_data_answer() =>
        Assert.Equal(RagService.NoDataAnswer,
            await _generator.GenerateAnswerAsync("pricing plans?", [new SearchHit("Unrelated text.", "s", 0.5)], Ct));

    [Fact]
    public async Task Stopword_only_overlap_gives_no_data_answer() =>
        Assert.Equal(RagService.NoDataAnswer,
            await _generator.GenerateAnswerAsync("Who is the best?", [new SearchHit("The office is in Berlin.", "s", 0.5)], Ct));

    [Fact]
    public async Task Citation_numbers_follow_retrieval_order_not_answer_order()
    {
        SearchHit[] context =
        [
            new("Our office is in Berlin.", "a.txt", 0.9),
            new("Support hours are 9 to 5 on weekdays for support.", "b.txt", 0.8),
        ];

        var answer = await _generator.GenerateAnswerAsync("What are the support hours?", context, Ct);

        Assert.StartsWith("[2] Support hours", answer);
        Assert.DoesNotContain("[1]", answer);
    }

    [Fact]
    public async Task At_most_three_passages_are_returned()
    {
        var context = Enumerable.Range(1, 5).Select(i => new SearchHit($"widget fact {i}", "s", 0.5)).ToList();
        var answer = await _generator.GenerateAnswerAsync("widget", context, Ct);
        Assert.Equal(3, answer.Split("widget fact").Length - 1);
    }
}
