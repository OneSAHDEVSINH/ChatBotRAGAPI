using System.Net;
using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class OllamaClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (OllamaClient Client, StubHttpHandler Handler) Create(
        Func<HttpRequestMessage, string?, HttpResponseMessage> respond, RagOptions? options = null)
    {
        var handler = new StubHttpHandler(respond);
        var client = new OllamaClient(new HttpClient(handler), Options.Create(options ?? new RagOptions
        {
            EmbeddingModel = "embed-model",
            ChatModel = "chat-model",
            OllamaBaseUrl = "http://ollama.test:11434",
            DisableThinking = true,
        }));
        return (client, handler);
    }

    [Fact]
    public async Task Batch_embedding_sends_all_inputs_in_one_request()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Json("""{"embeddings":[[0.1,0.2],[0.3,0.4]]}"""));

        var vectors = await client.EmbedBatchAsync(["a", "b"], Ct);

        Assert.Equal([[0.1f, 0.2f], [0.3f, 0.4f]], vectors);
        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal("http://ollama.test:11434/api/embed", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal("embed-model", json.RootElement.GetProperty("model").GetString());
        Assert.Equal(["a", "b"], json.RootElement.GetProperty("input").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Empty_batch_makes_no_request()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("should not be called"));
        Assert.Empty(await client.EmbedBatchAsync([], Ct));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Embedding_count_mismatch_throws()
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Json("""{"embeddings":[[0.1]]}"""));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.EmbedBatchAsync(["a", "b"], Ct));
    }

    [Fact]
    public async Task Error_responses_include_ollamas_message()
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Json("""{"error":"model 'embed-model' not found"}""", HttpStatusCode.NotFound));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.EmbedAsync("a", Ct));
        Assert.Contains("not found", ex.Message);
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }

    [Fact]
    public async Task Generation_numbers_passages_with_sources_and_disables_thinking()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Json("""{"response":"<think>hmm</think> Paris [2]"}"""));

        var answer = await client.GenerateAnswerAsync("Capital?",
            [new SearchHit("Berlin is in Germany.", "de.txt", 0.6), new SearchHit("Paris is in France.", "fr.txt", 0.5)], [], Ct);

        Assert.Equal("Paris [2]", answer);
        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.False(json.RootElement.GetProperty("think").GetBoolean());
        Assert.Equal("chat-model", json.RootElement.GetProperty("model").GetString());
        var prompt = json.RootElement.GetProperty("prompt").GetString()!;
        Assert.Contains("[1] (source: de.txt)\nBerlin is in Germany.", prompt);
        Assert.Contains("[2] (source: fr.txt)\nParis is in France.", prompt);
        Assert.Contains("Question: Capital?", prompt);
    }

    [Fact]
    public async Task Think_flag_is_omitted_when_thinking_is_allowed()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Json("""{"response":"ok"}"""),
            new RagOptions { OllamaBaseUrl = "http://ollama.test", DisableThinking = false });

        await client.GenerateAnswerAsync("q", [new SearchHit("t", "s", 1)], [], Ct);

        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.False(json.RootElement.TryGetProperty("think", out _));
    }

    [Fact]
    public async Task History_is_in_the_prompt_without_old_citation_markers()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Json("""{"response":"ok"}"""));

        await client.GenerateAnswerAsync("and the SLA?", [new SearchHit("t", "s", 1)],
            [new ChatMessage("user", "Price of Pro?"), new ChatMessage("assistant", "999 USD per month [1][2].")], Ct);

        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        var prompt = json.RootElement.GetProperty("prompt").GetString()!;
        Assert.Contains("User: Price of Pro?\nAssistant: 999 USD per month.\n\nQuestion: and the SLA?", prompt);
        Assert.True(prompt.IndexOf("Context:") < prompt.IndexOf("Conversation so far"));
    }

    [Fact]
    public async Task Streaming_yields_pieces_and_strips_split_think_tags()
    {
        const string ndjson = """
            {"response":"<thi","done":false}
            {"response":"nk>secret</th","done":false}
            {"response":"ink>\n Paris","done":false}
            {"response":" [2]","done":false}
            {"response":"","done":true}
            """;
        var (client, handler) = Create((_, _) => StubHttpHandler.Json(ndjson));

        var pieces = new List<string>();
        await foreach (var p in client.StreamAnswerAsync("Capital?", [new SearchHit("t", "s", 1)], [], Ct)) pieces.Add(p);

        Assert.Equal("Paris [2]", string.Concat(pieces));
        Assert.True(pieces.Count >= 2, "expected incremental pieces");
        using var json = JsonDocument.Parse(handler.Requests[0].Body!);
        Assert.True(json.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Streaming_error_line_throws()
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Json("""{"error":"model crashed"}"""));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in client.StreamAnswerAsync("q", [new SearchHit("t", "s", 1)], [], Ct)) { }
        });
        Assert.Contains("model crashed", ex.Message);
    }
}

public class ThinkTagFilterTests
{
    private static string Run(params string[] pieces)
    {
        var filter = new ThinkTagFilter();
        return string.Concat(pieces.Select(filter.Push)) + filter.Flush();
    }

    [Fact]
    public void Plain_text_passes_through() => Assert.Equal("Hello world", Run("Hel", "lo ", "world"));

    [Fact]
    public void Think_block_is_removed_even_when_tags_are_split() =>
        Assert.Equal("Answer", Run("<", "think", ">reasoning<", "/think>", "  Answer"));

    [Fact]
    public void Text_that_only_looks_like_a_tag_is_kept() => Assert.Equal("a < b <thin", Run("a < b ", "<thin"));

    [Fact]
    public void Unclosed_think_block_is_dropped() => Assert.Equal("ok", Run("ok<think>never closed"));

    [Fact]
    public void Leading_whitespace_is_trimmed() => Assert.Equal("x  y", Run("  ", "\n", "x  y"));
}
