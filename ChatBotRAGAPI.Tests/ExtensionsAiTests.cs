using System.Runtime.CompilerServices;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Services.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = ChatBotRAGAPI.Contracts.ChatMessage;

namespace ChatBotRAGAPI.Tests;

/// <summary>An IChatClient that answers from a script and records what it was asked.</summary>
internal sealed class FakeChatClient(Func<IList<AiChatMessage>, ChatOptions?, ChatResponse>? respond = null) : IChatClient
{
    public List<(IList<AiChatMessage> Messages, ChatOptions? Options)> Calls { get; } = [];
    public string[] StreamPieces { get; init; } = [];
    public Exception? StreamFailure { get; init; }
    public ChatFinishReason? StreamFinish { get; init; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add((list, options));
        return Task.FromResult(respond?.Invoke(list, options) ?? new ChatResponse(new AiChatMessage(ChatRole.Assistant, "ok")));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<AiChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add((messages.ToList(), options));
        foreach (var piece in StreamPieces)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, piece);
        }
        if (StreamFailure is not null) throw StreamFailure;
        if (StreamFinish is not null) yield return new ChatResponseUpdate { FinishReason = StreamFinish };
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

internal sealed class FakeEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
            values.Select(v => new Embedding<float>(Enumerable.Range(0, dimensions).Select(i => i == 0 ? v.Length : 0f).ToArray()))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

public class ChatClientAnswerGeneratorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly SearchHit[] Context = [new("Refund policy: a full refund within 30 days.", "handbook.txt", 0.8)];
    private static readonly ChatMessage[] NoHistory = [];

    [Fact]
    public async Task Sends_the_shared_prompt_with_the_answer_budget_and_no_sampling_settings()
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new AiChatMessage(ChatRole.Assistant, "A full refund within 30 days [1].")));
        var generator = new ChatClientAnswerGenerator(chat, "Anthropic", 16000);

        var answer = await generator.GenerateAnswerAsync("What is the refund policy?", Context, NoHistory, Ct);

        Assert.Equal("A full refund within 30 days [1].", answer);
        var (messages, options) = Assert.Single(chat.Calls);
        Assert.Equal(AnswerPrompt.Build("What is the refund policy?", Context, NoHistory), Assert.Single(messages).Text);
        Assert.Equal(16000, options!.MaxOutputTokens);
        Assert.Null(options.Temperature);
        Assert.Null(options.TopP);
    }

    [Fact]
    public async Task Inline_reasoning_is_removed()
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new AiChatMessage(ChatRole.Assistant, "<think>check passage 1</think>\nWithin 30 days [1].")));
        Assert.Equal("Within 30 days [1].", await new ChatClientAnswerGenerator(chat, "OpenAI", 100).GenerateAnswerAsync("q", Context, NoHistory, Ct));
    }

    [Fact]
    public async Task A_refusal_with_no_text_says_the_model_declined()
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new AiChatMessage(ChatRole.Assistant, "")) { FinishReason = ChatFinishReason.ContentFilter });
        Assert.Equal(ChatClientAnswerGenerator.DeclinedAnswer,
            await new ChatClientAnswerGenerator(chat, "Anthropic", 100).GenerateAnswerAsync("q", Context, NoHistory, Ct));
    }

    [Fact]
    public async Task Provider_errors_become_backend_errors_that_answer_503()
    {
        var chat = new FakeChatClient((_, _) => throw new InvalidOperationException("401 invalid x-api-key"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            new ChatClientAnswerGenerator(chat, "Anthropic", 100).GenerateAnswerAsync("q", Context, NoHistory, Ct));
        Assert.Equal("Anthropic request failed: 401 invalid x-api-key", error.Message);
    }

    [Fact]
    public async Task Streaming_yields_text_and_drops_think_tags_split_across_pieces()
    {
        var chat = new FakeChatClient { StreamPieces = ["<thi", "nk>weighing</th", "ink>Within ", "30 days [1]."] };
        var pieces = new List<string>();
        await foreach (var p in new ChatClientAnswerGenerator(chat, "OpenAI", 100).StreamAnswerAsync("q", Context, NoHistory, Ct)) pieces.Add(p);

        Assert.Equal("Within 30 days [1].", string.Concat(pieces));
    }

    [Fact]
    public async Task A_stream_failure_surfaces_as_a_backend_error()
    {
        var chat = new FakeChatClient { StreamPieces = ["Within "], StreamFailure = new TimeoutException("stream dropped") };
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in new ChatClientAnswerGenerator(chat, "OpenAI", 100).StreamAnswerAsync("q", Context, NoHistory, Ct)) { }
        });
    }

    [Fact]
    public async Task A_streamed_refusal_says_the_model_declined()
    {
        var chat = new FakeChatClient { StreamFinish = ChatFinishReason.ContentFilter };
        var pieces = new List<string>();
        await foreach (var p in new ChatClientAnswerGenerator(chat, "Anthropic", 100).StreamAnswerAsync("q", Context, NoHistory, Ct)) pieces.Add(p);
        Assert.Equal([ChatClientAnswerGenerator.DeclinedAnswer], pieces);
    }
}

public class EmbeddingGeneratorClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Returns_vectors_in_input_order_with_a_model_specific_id()
    {
        var client = new EmbeddingGeneratorClient(new FakeEmbeddingGenerator(4), "OpenAI", "text-embedding-3-small", 4);

        var vectors = await client.EmbedBatchAsync(["a", "bbb"], Ct);

        Assert.Equal([1f, 3f], vectors.Select(v => v[0]));
        Assert.Equal("openai:text-embedding-3-small:4", client.ModelId);
    }

    [Fact]
    public async Task A_dimension_mismatch_names_the_setting_to_fix()
    {
        var client = new EmbeddingGeneratorClient(new FakeEmbeddingGenerator(1536), "OpenAI", "text-embedding-3-small", 768);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedBatchAsync(["a"], Ct));
        Assert.Contains("Set Rag:EmbeddingDimensions to 1536", error.Message);
    }
}

public class ChatClientImageTextReaderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "image/png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    public async Task Sends_the_page_image_with_its_media_type_and_the_ocr_prompt(byte[] image, string mediaType)
    {
        var chat = new FakeChatClient((_, _) => new ChatResponse(new AiChatMessage(ChatRole.Assistant, " Invoice 0042 ")));

        var text = await new ChatClientImageTextReader(chat, "Anthropic", 2048).ReadTextAsync(image, Ct);

        Assert.Equal("Invoice 0042", text);
        var contents = Assert.Single(Assert.Single(chat.Calls).Messages).Contents;
        Assert.Equal(OcrPrompt.Text, Assert.IsType<TextContent>(contents[0]).Text);
        var data = Assert.IsType<DataContent>(contents[1]);
        Assert.Equal(mediaType, data.MediaType);
        Assert.Equal(image, data.Data.ToArray());
        Assert.Equal(2048, Assert.Single(chat.Calls).Options!.MaxOutputTokens);
    }
}

public class AiProviderWiringTests
{
    private static RagApiFactory App(Dictionary<string, string> settings)
    {
        settings["Rag:UseLocalAiFallback"] = "false";
        return new RagApiFactory(settings: settings);
    }

    [Fact]
    public void Ollama_stays_the_default()
    {
        using var app = App([]);
        Assert.IsType<OllamaClient>(app.Services.GetRequiredService<IAnswerGenerator>());
        Assert.IsType<OllamaClient>(app.Services.GetRequiredService<IEmbeddingClient>());
        Assert.Null(app.Services.GetService<IChatClient>());
    }

    [Fact]
    public void Open_ai_answers_through_an_ichatclient_and_can_embed()
    {
        using var app = App(new()
        {
            ["Rag:ChatProvider"] = "openai", ["Rag:OpenAI:ApiKey"] = "test-key", ["Rag:ChatModel"] = "gpt-test",
            ["Rag:EmbeddingProvider"] = "OpenAI", ["Rag:EmbeddingModel"] = "text-embedding-3-small", ["Rag:EmbeddingDimensions"] = "1536",
        });

        Assert.IsType<ChatClientAnswerGenerator>(app.Services.GetRequiredService<IAnswerGenerator>());
        Assert.NotNull(app.Services.GetRequiredService<IChatClient>());
        var embedder = Assert.IsType<EmbeddingGeneratorClient>(app.Services.GetRequiredService<IEmbeddingClient>());
        Assert.Equal("openai:text-embedding-3-small:1536", embedder.ModelId);
    }

    [Fact]
    public void Anthropic_answers_while_ollama_embeds_and_ocr_uses_claude()
    {
        using var app = App(new()
        {
            ["Rag:ChatProvider"] = "Anthropic", ["Rag:Anthropic:ApiKey"] = "test-key", ["Rag:ChatModel"] = "claude-opus-5-5",
            ["Rag:OcrModel"] = "claude-opus-5-5",
        });

        Assert.IsType<ChatClientAnswerGenerator>(app.Services.GetRequiredService<IAnswerGenerator>());
        Assert.IsType<OllamaClient>(app.Services.GetRequiredService<IEmbeddingClient>());
        Assert.IsType<ChatClientImageTextReader>(app.Services.GetRequiredService<IImageTextReader>());
        Assert.True(app.Services.GetRequiredService<FileTextExtractor>().OcrEnabled);
    }

    [Theory]
    [InlineData("Rag:EmbeddingProvider", "Anthropic", "Anthropic has no embeddings API")]
    [InlineData("Rag:ChatProvider", "Gemini", "use one of: Ollama, OpenAI, Anthropic")]
    [InlineData("Rag:EmbeddingProvider", "Cohere", "use one of: Ollama, OpenAI")]
    public void Misconfiguration_fails_at_startup_with_the_fix(string key, string value, string message)
    {
        using var app = App(new() { [key] = value });
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains(message, error.ToString());
    }

    [Fact]
    public void Open_ai_without_a_key_fails_at_startup()
    {
        Assert.SkipWhen(!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OPENAI_API_KEY")), "OPENAI_API_KEY is set on this machine");
        using var app = App(new() { ["Rag:ChatProvider"] = "OpenAI" });
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.Contains("needs an API key", error.ToString());
    }
}
