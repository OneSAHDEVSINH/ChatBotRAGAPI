using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Net;
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

    public bool IsPersistent => true;

    /// <summary>The collection must exist and answer; a missing one is a failure (the worker creates it at startup).</summary>
    public async Task PingAsync(CancellationToken ct)
    {
        using var response = await _httpClient.GetAsync($"/collections/{_options.QdrantCollection}", ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Qdrant collection '{_options.QdrantCollection}' answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    public async Task EnsureCollectionAsync(CancellationToken ct)
    {
        var collectionPath = $"/collections/{_options.QdrantCollection}";

        using (var existing = await _httpClient.GetAsync(collectionPath, ct))
        {
            if (existing.IsSuccessStatusCode)
            {
                await VerifyVectorSizeAsync(existing, ct);
                return;
            }

            if (existing.StatusCode != HttpStatusCode.NotFound)
                existing.EnsureSuccessStatusCode();
        }

        var body = JsonSerializer.Serialize(new
        {
            vectors = new { size = _options.EmbeddingDimensions, distance = "Cosine" }
        });

        using var response = await _httpClient.PutAsync(
            collectionPath,
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        // 409 = created concurrently by another instance; treat as success.
        if (response.StatusCode == HttpStatusCode.Conflict) return;
        response.EnsureSuccessStatusCode();
    }

    private async Task VerifyVectorSizeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        // Unnamed-vector collections expose result.config.params.vectors.size.
        if (doc.RootElement.TryGetProperty("result", out var result) &&
            result.TryGetProperty("config", out var config) &&
            config.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("vectors", out var vectors) &&
            vectors.ValueKind == JsonValueKind.Object &&
            vectors.TryGetProperty("size", out var sizeElement) &&
            sizeElement.TryGetInt32(out var size) &&
            size != _options.EmbeddingDimensions)
        {
            throw new InvalidOperationException(
                $"Qdrant collection '{_options.QdrantCollection}' has vector size {size}, " +
                $"but Rag:EmbeddingDimensions is {_options.EmbeddingDimensions}. " +
                "Use a different Rag:QdrantCollection or delete the existing collection.");
        }
    }

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        if (points.Count == 0) return;

        var body = JsonSerializer.Serialize(new
        {
            points = points.Select(p => new
            {
                id = p.Id.ToString(),
                vector = p.Vector,
                payload = new { text = p.Text, source = p.Source }
            })
        });

        using var response = await _httpClient.PutAsync(
            $"/collections/{_options.QdrantCollection}/points?wait=true",
            new StringContent(body, Encoding.UTF8, "application/json"),
            ct);

        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;

        var body = JsonSerializer.Serialize(new { points = ids.Select(id => id.ToString()) });

        using var response = await _httpClient.PostAsync(
            $"/collections/{_options.QdrantCollection}/points/delete?wait=true",
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
            var payload = item.GetProperty("payload");
            var text = payload.GetProperty("text").GetString() ?? string.Empty;
            var source = payload.TryGetProperty("source", out var s) ? s.GetString() ?? string.Empty : string.Empty;
            hits.Add(new SearchHit(text, source, score));
        }

        return hits;
    }
}