using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

public sealed partial class OllamaClient : IEmbeddingClient, IAnswerGenerator
{
    private readonly HttpClient _httpClient;
    private readonly RagOptions _options;

    public OllamaClient(HttpClient httpClient, IOptions<RagOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.OllamaBaseUrl);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        (await EmbedBatchAsync([text], ct))[0];

    /// <summary>Uses /api/embed, which accepts many inputs per request (the old /api/embeddings is deprecated and single-input).</summary>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];

        var payload = JsonSerializer.Serialize(new
        {
            model = _options.EmbeddingModel,
            input = texts,
            keep_alive = _options.KeepAlive
        });

        using var response = await _httpClient.PostAsync(
            "/api/embed",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            ct);
        await EnsureSuccessAsync(response, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var vectors = doc.RootElement.GetProperty("embeddings")
            .EnumerateArray()
            .Select(e => e.EnumerateArray().Select(x => x.GetSingle()).ToArray())
            .ToList();

        if (vectors.Count != texts.Count)
            throw new InvalidOperationException($"Ollama returned {vectors.Count} embeddings for {texts.Count} inputs.");

        return vectors;
    }

    public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, CancellationToken ct)
    {
        var passages = string.Join("\n\n", context.Select((h, i) => $"[{i + 1}] (source: {h.Source})\n{h.Text.Trim()}"));

        var prompt = $"""
        Answer the question using ONLY the numbered context passages below.
        - If the passages do not contain the answer, reply exactly: I don't have enough data.
        - Be concise. After each fact, cite the passage numbers you used in square brackets, e.g. [1] or [1][2].

        Context:
        {passages}

        Question: {question}
        Answer:
        """;

        return GenerateAsync(prompt, ct);
    }

    private async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.ChatModel,
            ["prompt"] = prompt,
            ["stream"] = false,
            ["keep_alive"] = _options.KeepAlive,
            ["options"] = new
            {
                num_predict = _options.MaxGenerateTokens,
                num_ctx = _options.NumCtx,
                temperature = 0.1
            }
        };

        // Thinking models (qwen3, deepseek-r1, ...) otherwise spend the num_predict budget on hidden
        // reasoning and return an empty or truncated answer.
        if (_options.DisableThinking) body["think"] = false;

        using var response = await _httpClient.PostAsync(
            "/api/generate",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
            ct);
        await EnsureSuccessAsync(response, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var answer = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

        // Some models/versions inline their reasoning instead of using the separate "thinking" field.
        return ThinkBlock().Replace(answer, string.Empty).Trim();
    }

    /// <summary>Like EnsureSuccessStatusCode, but keeps Ollama's error text (e.g. "model not found").</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Ollama returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}",
            null,
            response.StatusCode);
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();
}
