using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;

namespace ChatBotRAGAPI.Services;

public sealed class QdrantClient : IVectorStore
{
    private readonly HttpClient _httpClient;
    private readonly RagOptions _options;

    public QdrantClient(HttpClient httpClient, IOptions<RagOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.BaseAddress = new Uri(_options.QdrantBaseUrl);
    }

    public async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            vectors = new { size = _options.EmbeddingDimensions, distance = "Cosine" }
        });

        using var response = await _httpClient.PutAsync(
            $"/collections/{_options.QdrantCollection}",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task UpsertAsync(Guid id, float[] vector, string text, string source, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            points = new[]
            {
                new
                {
                    id = id.ToString(),
                    vector,
                    payload = new { text, source }
                }
            }
        });

        using var response = await _httpClient.PutAsync(
            $"/collections/{_options.QdrantCollection}/points?wait=true",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { vector, limit = topK, with_payload = true });

        using var response = await _httpClient.PostAsync(
            $"/collections/{_options.QdrantCollection}/points/search",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        var hits = new List<SearchHit>();
        foreach (var item in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var score = item.GetProperty("score").GetDouble();
            var text = item.GetProperty("payload").GetProperty("text").GetString() ?? string.Empty;
            hits.Add(new SearchHit(text, score));
        }

        return hits;
    }
}