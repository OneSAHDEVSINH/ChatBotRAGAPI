using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace ChatBotRAGAPI.Services;

public sealed class OllamaClient : IEmbeddingClient, IAnswerGenerator
{
    private readonly HttpClient _httpClient;
    private readonly RagOptions _options;

    public OllamaClient(HttpClient httpClient, IOptions<RagOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.OllamaBaseUrl);
    }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { model = _options.EmbeddingModel, prompt = text });
        using var response = await _httpClient.PostAsync("/api/embeddings", new StringContent(payload, Encoding.UTF8, "application/json"), ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
    }

    public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct)
    {
        var prompt = $"""
        You are a strict assistant. Answer ONLY from context.
        If context is insufficient, say: "I don't have enough data."

        Context:
        {string.Join("\n---\n", context)}

        Question:
        {question}
        """;

        return GenerateAsync(prompt, ct);
    }

    private async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = _options.ChatModel,
            prompt,
            stream = false,
            keep_alive = _options.KeepAlive,
            options = new
            {
                num_predict = _options.MaxGenerateTokens,
                num_ctx = _options.NumCtx,
                temperature = 0.1
            }
        });

        using var response = await _httpClient.PostAsync(
            "/api/generate",
            new StringContent(payload, Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("response").GetString() ?? string.Empty;
    }
}