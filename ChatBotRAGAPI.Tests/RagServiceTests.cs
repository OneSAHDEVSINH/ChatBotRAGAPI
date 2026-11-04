using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class RagServiceTests
{
    private sealed class FakeEmbedder : IEmbeddingClient
    {
        public string? LastText;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            LastText = text;
            return Task.FromResult(new float[] { 1 });
        }
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1 }).ToList());
    }

    private sealed class FakeStore(params SearchHit[] hits) : IVectorStore
    {
        public int? RequestedTopK;
        public bool IsPersistent => false;
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct)
        {
            RequestedTopK = topK;
            return Task.FromResult<IReadOnlyList<SearchHit>>(hits);
        }
    }

    private sealed class RecordingGenerator : IAnswerGenerator
    {
        public IReadOnlyList<SearchHit>? Context;
        public IReadOnlyList<ChatMessage>? History;

        public Task<string> GenerateAnswerAsync(
            string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct)
        {
            Context = context;
            History = history;
            return Task.FromResult("generated [1]");
        }

        public async IAsyncEnumerable<string> StreamAnswerAsync(
            string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Context = context;
            History = history;
            await Task.Yield();
            yield return "gen";
            yield return "erated";
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeEmbedder _embedder = new();

    private RagService Service(FakeStore store, RecordingGenerator generator, RagOptions? options = null) =>
        new(_embedder, generator, store, Options.Create(options ?? new RagOptions { UseLocalAiFallback = false, MinScore = 0.45, TopK = 3 }));

    private static async Task<string> Collect(IAsyncEnumerable<string> pieces)
    {
        var all = "";
        await foreach (var p in pieces) all += p;
        return all;
    }

    [Fact]
    public async Task Follow_up_is_searched_together_with_the_previous_user_question()
    {
        ChatMessage[] history = [new("user", "Tell me about the Pro plan"), new("assistant", "It costs 999 [1].")];
        await Service(new FakeStore(), new RecordingGenerator()).AskAsync("and the SLA?", history, Ct);
        Assert.Equal("Tell me about the Pro plan\nand the SLA?", _embedder.LastText);

        await Service(new FakeStore(), new RecordingGenerator()).AskAsync("standalone", null, Ct);
        Assert.Equal("standalone", _embedder.LastText);
    }

    [Fact]
    public async Task Only_recent_history_reaches_the_generator()
    {
        var history = Enumerable.Range(1, 10).Select(i => new ChatMessage(i % 2 == 1 ? "user" : "assistant", $"m{i}")).ToList();
        var generator = new RecordingGenerator();
        var options = new RagOptions { UseLocalAiFallback = false, MinScore = 0, MaxHistoryMessages = 4 };

        await Service(new FakeStore(new SearchHit("t", "s", 0.9)), generator, options).AskAsync("q", history, Ct);

        Assert.Equal(["m7", "m8", "m9", "m10"], generator.History!.Select(m => m.Content));
    }

    [Fact]
    public async Task Streaming_returns_sources_up_front_and_pieces_from_the_generator()
    {
        var generator = new RecordingGenerator();
        var answer = await Service(new FakeStore(new SearchHit("best", "a.txt", 0.8)), generator).AskStreamingAsync("q", null, Ct);

        Assert.Equal([new ContextSource(1, "a.txt", 0.8)], answer.Sources);
        Assert.Equal("generated", await Collect(answer.Pieces));
    }

    [Fact]
    public async Task Streaming_without_relevant_hits_yields_no_data_without_generator()
    {
        var generator = new RecordingGenerator();
        var answer = await Service(new FakeStore(new SearchHit("weak", "a.txt", 0.1)), generator).AskStreamingAsync("q", null, Ct);

        Assert.Empty(answer.Sources);
        Assert.Equal(RagService.NoDataAnswer, await Collect(answer.Pieces));
        Assert.Null(generator.Context);
    }

    [Fact]
    public async Task No_hit_above_threshold_answers_without_calling_generator()
    {
        var generator = new RecordingGenerator();
        var result = await Service(new FakeStore(new SearchHit("weak", "a.txt", 0.40)), generator).AskAsync("q", null, Ct);

        Assert.Equal(RagService.NoDataAnswer, result.Answer);
        Assert.Empty(result.Context);
        Assert.Empty(result.Sources);
        Assert.Null(generator.Context);
    }

    [Fact]
    public async Task Weak_hits_are_dropped_and_sources_are_numbered_like_citations()
    {
        var store = new FakeStore(
            new SearchHit("best", "a.txt", 0.712345),
            new SearchHit("good", "https://example.com/", 0.5),
            new SearchHit("weak", "c.txt", 0.3));
        var generator = new RecordingGenerator();

        var result = await Service(store, generator).AskAsync("q", null, Ct);

        Assert.Equal(3, store.RequestedTopK);
        Assert.Equal("generated [1]", result.Answer);
        Assert.Equal(["best", "good"], generator.Context!.Select(h => h.Text));
        Assert.Equal(["best", "good"], result.Context);
        Assert.Equal(
            [new ContextSource(1, "a.txt", 0.7123), new ContextSource(2, "https://example.com/", 0.5)],
            result.Sources);
    }

    [Theory]
    [InlineData(true, 0.1, true)]    // local fallback uses LocalMinScore (0.08)
    [InlineData(false, 0.1, false)]  // Ollama uses MinScore (0.45)
    public void Effective_threshold_depends_on_backend(bool localFallback, double score, bool kept)
    {
        var options = new RagOptions { UseLocalAiFallback = localFallback, MinScore = 0.45, LocalMinScore = 0.08 };
        Assert.Equal(kept, score >= options.EffectiveMinScore);
    }

    [Fact]
    public async Task Local_fallback_threshold_is_applied()
    {
        var generator = new RecordingGenerator();
        var options = new RagOptions { UseLocalAiFallback = true, LocalMinScore = 0.08, MinScore = 0.45 };
        var result = await Service(new FakeStore(new SearchHit("t", "s", 0.1)), generator, options).AskAsync("q", null, Ct);
        Assert.Single(result.Sources);
    }
}
