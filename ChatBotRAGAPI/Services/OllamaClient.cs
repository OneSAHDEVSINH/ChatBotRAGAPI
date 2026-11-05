using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

public sealed partial class OllamaClient : IEmbeddingClient, IAnswerGenerator, IImageTextReader
{
    private readonly HttpClient _httpClient;
    private readonly RagOptions _options;

    public OllamaClient(HttpClient httpClient, IOptions<RagOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.OllamaBaseUrl);
    }

    public string ModelId => $"ollama:{_options.EmbeddingModel}:{_options.EmbeddingDimensions}";

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

    public async Task<string> GenerateAnswerAsync(
        string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        using var response = await _httpClient.PostAsync(
            "/api/generate", GenerateBody(AnswerPrompt.Build(question, context, history), stream: false), ct);
        await EnsureSuccessAsync(response, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var answer = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;

        // Some models/versions inline their reasoning instead of using the separate "thinking" field.
        return ThinkBlock().Replace(answer, string.Empty).Trim();
    }

    /// <summary>Streams /api/generate, which returns one JSON object per line: {"response":"piece","done":false}.</summary>
    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/generate")
        {
            Content = GenerateBody(AnswerPrompt.Build(question, context, history), stream: true)
        };
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, ct);

        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        var filter = new ThinkTagFilter();

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var error))
                throw new HttpRequestException($"Ollama stream failed: {error.GetString()}");

            if (root.TryGetProperty("response", out var piece) && filter.Push(piece.GetString() ?? "") is { Length: > 0 } text)
                yield return text;

            if (root.TryGetProperty("done", out var done) && done.GetBoolean()) break;
        }

        if (filter.Flush() is { Length: > 0 } rest) yield return rest.TrimEnd();
    }

    private StringContent GenerateBody(string prompt, bool stream)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.ChatModel,
            ["prompt"] = prompt,
            ["stream"] = stream,
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

        return new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
    }


    /// <summary>OCR through an Ollama vision model (<see cref="RagOptions.OcrModel"/>), one page image per call.</summary>
    public async Task<string> ReadTextAsync(byte[] image, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = _options.OcrModel,
            ["prompt"] = OcrPrompt.Text,
            ["images"] = new[] { Convert.ToBase64String(image) },
            ["stream"] = false,
            ["keep_alive"] = _options.KeepAlive,
            ["options"] = new { num_predict = _options.OcrMaxTokens, num_ctx = Math.Max(_options.NumCtx, 4096), temperature = 0 }
        };
        if (_options.DisableThinking) body["think"] = false;

        using var response = await _httpClient.PostAsync(
            "/api/generate", new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"), ct);
        await EnsureSuccessAsync(response, ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var text = doc.RootElement.GetProperty("response").GetString() ?? string.Empty;
        return ThinkBlock().Replace(text, string.Empty).Trim();
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
