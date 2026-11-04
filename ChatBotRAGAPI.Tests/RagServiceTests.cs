using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class RagServiceTests
{
    private sealed class FakeEmbedder : IEmbeddingClient
    {
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(new float[] { 1 });
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
        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, CancellationToken ct)
        {
            Context = context;
            return Task.FromResult("generated [1]");
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RagService Service(FakeStore store, RecordingGenerator generator, RagOptions? options = null) =>
        new(new FakeEmbedder(), generator, store, Options.Create(options ?? new RagOptions { UseLocalAiFallback = false, MinScore = 0.45, TopK = 3 }));

    [Fact]
    public async Task No_hit_above_threshold_answers_without_calling_generator()
    {
        var generator = new RecordingGenerator();
        var result = await Service(new FakeStore(new SearchHit("weak", "a.txt", 0.40)), generator).AskAsync("q", Ct);

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

        var result = await Service(store, generator).AskAsync("q", Ct);

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
        var result = await Service(new FakeStore(new SearchHit("t", "s", 0.1)), generator, options).AskAsync("q", Ct);
        Assert.Single(result.Sources);
    }
}
