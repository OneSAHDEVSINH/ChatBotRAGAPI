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

public class QueryRewriteTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ChatMessage[] History =
    [
        new("user", "Which support plans do you offer?"),
        new("assistant", "There is a Silver and a Gold SLA [1]; Gold adds emergency support [2]."),
    ];

    // ---------- prompt and cleanup ----------

    [Theory]
    [InlineData("What does the Gold SLA cost?", "What does the Gold SLA cost?")]
    [InlineData("  \n\"What does the Gold SLA cost?\"\n", "What does the Gold SLA cost?")]
    [InlineData("Standalone question: What does the Gold SLA cost?", "What does the Gold SLA cost?")]
    [InlineData("<think>they mean Gold</think>What does the Gold SLA cost?", "What does the Gold SLA cost?")]
    [InlineData("What does the Gold SLA cost?\nThis replaces 'it' with the plan.", "What does the Gold SLA cost?")]
    public void Clean_keeps_only_the_question(string output, string expected) =>
        Assert.Equal(expected, QueryRewritePrompt.Clean(output));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    [InlineData("<think>hmm</think>")]
    public void Clean_rejects_empty_output(string? output) => Assert.Null(QueryRewritePrompt.Clean(output));

    [Fact]
    public void Clean_rejects_a_rambling_answer() => Assert.Null(QueryRewritePrompt.Clean(new string('x', 501)));

    [Fact]
    public void The_prompt_has_the_conversation_without_citations_and_cuts_long_turns()
    {
        var history = new ChatMessage[] { History[0], new("assistant", "Gold [2] " + new string('y', 2000)) };

        var prompt = QueryRewritePrompt.Build("And what does it cost?", history);

        Assert.Contains("User: Which support plans do you offer?", prompt);
        Assert.Contains("Assistant: Gold " , prompt);
        Assert.DoesNotContain("[2]", prompt);
        Assert.DoesNotContain(new string('y', 700), prompt);
        Assert.Contains("Last question: And what does it cost?", prompt);
    }

    // ---------- RagService ----------

    private sealed class RecordingEmbedder : IEmbeddingClient
    {
        public List<string> Texts { get; } = [];
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) { Texts.Add(text); return Task.FromResult(new float[] { 1 }); }
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[] { 1 }).ToList());
    }

    private sealed class OneHitStore : IVectorStore
    {
        public bool IsPersistent => false;
        public Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, string knowledgeBase, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SearchHit>>([new SearchHit("Gold costs 900 a month.", "plans.txt", 0.9)]);
    }

    private sealed class EchoQuestion : IAnswerGenerator
    {
        public string? Question;
        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct)
        {
            Question = question;
            return Task.FromResult("900 [1]");
        }
        public IAsyncEnumerable<string> StreamAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FakeRewriter(Func<string?> rewrite) : IQueryRewriter
    {
        public int Calls;
        public Task<string?> RewriteAsync(string question, IReadOnlyList<ChatMessage> history, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(rewrite());
        }
    }

    private static (RagService Rag, RecordingEmbedder Embedder, EchoQuestion Generator) Rag(IQueryRewriter? rewriter, bool enabled = true)
    {
        var embedder = new RecordingEmbedder();
        var generator = new EchoQuestion();
        var options = Options.Create(new RagOptions { HybridSearch = false, MinScore = 0.1, RewriteFollowUps = enabled });
        return (new RagService(embedder, generator, new OneHitStore(), options, rewriter: rewriter), embedder, generator);
    }

    [Fact]
    public async Task A_follow_up_is_searched_as_the_rewritten_question_but_answered_as_asked()
    {
        var rewriter = new FakeRewriter(() => "What does the Gold SLA cost?");
        var (rag, embedder, generator) = Rag(rewriter);

        await rag.AskAsync("And what does it cost?", History, Ct);

        Assert.Equal(["What does the Gold SLA cost?"], embedder.Texts);
        Assert.Equal("And what does it cost?", generator.Question);
    }

    [Fact]
    public async Task The_first_question_is_never_rewritten()
    {
        var rewriter = new FakeRewriter(() => "should not be used");
        var (rag, embedder, _) = Rag(rewriter);

        await rag.AskAsync("Which support plans do you offer?", [], Ct);

        Assert.Equal(0, rewriter.Calls);
        Assert.Equal(["Which support plans do you offer?"], embedder.Texts);
    }

    [Fact]
    public async Task Unusable_output_falls_back_to_the_previous_question_plus_the_follow_up()
    {
        var (rag, embedder, _) = Rag(new FakeRewriter(() => null));

        await rag.AskAsync("And what does it cost?", History, Ct);

        Assert.Equal(["Which support plans do you offer?\nAnd what does it cost?"], embedder.Texts);
    }

    [Fact]
    public async Task A_failing_rewriter_does_not_fail_the_question()
    {
        var (rag, embedder, _) = Rag(new FakeRewriter(() => throw new HttpRequestException("model down")));

        var response = await rag.AskAsync("And what does it cost?", History, Ct);

        Assert.Equal("900 [1]", response.Answer);
        Assert.Equal(["Which support plans do you offer?\nAnd what does it cost?"], embedder.Texts);
    }

    [Fact]
    public async Task Rewriting_can_be_switched_off()
    {
        var rewriter = new FakeRewriter(() => "What does the Gold SLA cost?");
        var (rag, embedder, _) = Rag(rewriter, enabled: false);

        await rag.AskAsync("And what does it cost?", History, Ct);

        Assert.Equal(0, rewriter.Calls);
        Assert.Equal(["Which support plans do you offer?\nAnd what does it cost?"], embedder.Texts);
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        var (rag, _, _) = Rag(new FakeRewriter(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rag.AskAsync("And what does it cost?", History, cts.Token));
    }

    // ---------- providers ----------

    [Fact]
    public async Task Ollama_rewrites_with_a_short_deterministic_call()
    {
        JsonElement? sent = null;
        var handler = new StubHttpHandler((_, body) =>
        {
            sent = JsonDocument.Parse(body!).RootElement.Clone();
            return StubHttpHandler.Json("""{"response":"\"What does the Gold SLA cost?\"","done":true}""");
        });
        var ollama = new OllamaClient(new HttpClient(handler) { BaseAddress = new Uri("http://ollama.test") },
            Options.Create(new RagOptions { ChatModel = "chat-model", DisableThinking = true }));

        var rewritten = await ollama.RewriteAsync("And what does it cost?", History, Ct);

        Assert.Equal("What does the Gold SLA cost?", rewritten);
        Assert.Equal("chat-model", sent!.Value.GetProperty("model").GetString());
        Assert.False(sent.Value.GetProperty("stream").GetBoolean());
        Assert.False(sent.Value.GetProperty("think").GetBoolean());
        Assert.Equal(QueryRewritePrompt.MaxTokens, sent.Value.GetProperty("options").GetProperty("num_predict").GetInt32());
        Assert.Equal(0, sent.Value.GetProperty("options").GetProperty("temperature").GetDouble());
    }

    [Fact]
    public async Task Cloud_providers_rewrite_through_the_chat_client()
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new AiChatMessage(ChatRole.Assistant, "What does the Gold SLA cost?")));
        var rewriter = new ChatClientQueryRewriter(chat, AiProviders.Anthropic, 16000);

        Assert.Equal("What does the Gold SLA cost?", await rewriter.RewriteAsync("And what does it cost?", History, Ct));
        var (messages, options) = Assert.Single(chat.Calls);
        Assert.Contains("Last question: And what does it cost?", messages.Single().Text);
        Assert.Equal(16000, options!.MaxOutputTokens);
        Assert.Null(options.Temperature);
    }
}
