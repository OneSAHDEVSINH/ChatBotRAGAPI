using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Services.Ai;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = ChatBotRAGAPI.Contracts.ChatMessage;

namespace ChatBotRAGAPI.Tests;

public class RerankTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---------- prompt and parser ----------

    [Theory]
    [InlineData("3, 1", 4, new[] { 2, 0 })]
    [InlineData("<think>2 looks best</think>Numbers: [2], [4]", 4, new[] { 1, 3 })]
    [InlineData("2, 2, 9, 0, 1", 4, new[] { 1, 0 })]   // duplicates and out-of-range numbers are ignored
    [InlineData("0", 4, new int[0])]
    [InlineData("none of them", 4, new int[0])]
    [InlineData(null, 4, new int[0])]
    public void Parse_keeps_valid_numbers_in_order(string? output, int count, int[] expected) =>
        Assert.Equal(expected, RerankPrompt.Parse(output, count));

    [Fact]
    public void The_prompt_numbers_the_passages_and_cuts_long_ones()
    {
        var prompt = RerankPrompt.Build("What does Gold cost?", ["Gold costs 900.", new string('x', 2000)]);

        Assert.Contains("Question: What does Gold cost?", prompt);
        Assert.Contains("[1] Gold costs 900.", prompt);
        Assert.Contains("[2] " + new string('x', 700) + "…", prompt);
        Assert.DoesNotContain(new string('x', 701), prompt);
    }

    // ---------- RagService ----------

    private sealed class Store(params SearchHit[] hits) : IVectorStore
    {
        public int RequestedTopK;
        public bool IsPersistent => false;
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, string knowledgeBase, CancellationToken ct)
        {
            RequestedTopK = topK;
            return Task.FromResult<IReadOnlyList<SearchHit>>(hits.Take(topK).ToList());
        }
    }

    private sealed class Embedder : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(new float[] { 1 });
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1 }).ToList());
    }

    private sealed class Echo : IAnswerGenerator
    {
        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct) =>
            Task.FromResult("ok [1]");
        public IAsyncEnumerable<string> StreamAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeReranker(Func<IReadOnlyList<string>, IReadOnlyList<int>> rank) : IReranker
    {
        public IReadOnlyList<string>? Seen;
        public Task<IReadOnlyList<int>> RankAsync(string question, IReadOnlyList<string> passages, CancellationToken ct)
        {
            Seen = passages;
            return Task.FromResult(rank(passages));
        }
    }

    private static readonly SearchHit[] Hits = Enumerable.Range(1, 8)
        .Select(i => new SearchHit($"passage {i}", $"doc{i}.txt", 0.9 - i * 0.01)).ToArray();

    private static (RagService Rag, Store Store) Rag(IReranker? reranker, bool enabled = true)
    {
        var store = new Store(Hits);
        var options = Options.Create(new RagOptions { HybridSearch = false, MinScore = 0.1, TopK = 3, Rerank = enabled, RerankCandidates = 6 });
        return (new RagService(new Embedder(), new Echo(), store, options, reranker: reranker), store);
    }

    [Fact]
    public async Task The_reranker_sees_more_candidates_and_its_order_is_kept()
    {
        var reranker = new FakeReranker(_ => [5, 0, 2, 4]);
        var (rag, store) = Rag(reranker);

        var response = await rag.AskAsync("q", null, Ct);

        Assert.Equal(6, store.RequestedTopK);
        Assert.Equal(6, reranker.Seen!.Count);
        Assert.Equal(["doc6.txt", "doc1.txt", "doc3.txt"], response.Sources.Select(s => s.Source)); // TopK of its order
        Assert.Equal([1, 2, 3], response.Sources.Select(s => s.Id));
    }

    [Fact]
    public async Task The_reranker_may_prune_to_fewer_than_TopK()
    {
        var (rag, _) = Rag(new FakeReranker(_ => [1]));
        Assert.Equal(["doc2.txt"], (await rag.AskAsync("q", null, Ct)).Sources.Select(s => s.Source));
    }

    [Theory]
    [InlineData(false)] // "none of these help": not trusted to refuse on its own
    [InlineData(true)]  // the call failed
    public async Task An_empty_or_failed_ranking_keeps_the_retrieval_order(bool fail)
    {
        var (rag, _) = Rag(new FakeReranker(_ => fail ? throw new HttpRequestException("down") : []));

        var response = await rag.AskAsync("q", null, Ct);

        Assert.Equal(["doc1.txt", "doc2.txt", "doc3.txt"], response.Sources.Select(s => s.Source));
        Assert.Equal("ok [1]", response.Answer);
    }

    [Fact]
    public async Task Off_by_default_and_then_never_called()
    {
        Assert.False(new RagOptions().Rerank);
        var reranker = new FakeReranker(_ => [5]);
        var (rag, store) = Rag(reranker, enabled: false);

        var response = await rag.AskAsync("q", null, Ct);

        Assert.Null(reranker.Seen);
        Assert.Equal(3, store.RequestedTopK);
        Assert.Equal(["doc1.txt", "doc2.txt", "doc3.txt"], response.Sources.Select(s => s.Source));
    }

    // ---------- providers ----------

    [Fact]
    public async Task Ollama_ranks_with_a_short_deterministic_call()
    {
        JsonElement? sent = null;
        var handler = new StubHttpHandler((_, body) =>
        {
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return StubHttpHandler.Json("""{"response":"2, 1","done":true}""");
        });
        var ollama = new OllamaClient(new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test") },
            Options.Create(new RagOptions { ChatModel = "chat-model", DisableThinking = true }));

        Assert.Equal([1, 0], await ollama.RankAsync("q", ["a", "b"], Ct));
        Assert.Equal(RerankPrompt.MaxTokens, sent!.Value.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0, sent.Value.GetProperty("options").GetProperty("temperature").GetDouble());
        Assert.False(sent.Value.GetProperty("think").GetBoolean());
    }

    [Fact]
    public async Task Cloud_providers_rank_through_the_chat_client()
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new AiChatMessage(ChatRole.Assistant, "1")));
        var reranker = new ChatClientReranker(chat, AiProviders.OpenAI, 16000);

        Assert.Equal([0], await reranker.RankAsync("q", ["a", "b"], Ct));
        Assert.Equal(16000, Assert.Single(chat.Calls).Options!.MaxOutputTokens);
    }
}
