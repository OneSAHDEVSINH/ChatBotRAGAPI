using Microsoft.Extensions.AI;

namespace ChatBotRAGAPI.Services.Ai;

/// <summary>
/// Embeds through any Microsoft.Extensions.AI <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/> (OpenAI, Azure
/// OpenAI, OpenAI-compatible servers). Vectors must have Rag:EmbeddingDimensions entries: a Qdrant collection is
/// created with that size and cached vectors are tagged with it, so a mismatch fails loudly instead of mixing spaces.
/// </summary>
public sealed class EmbeddingGeneratorClient : IEmbeddingClient
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string _provider;
    private readonly int _dimensions;

    public EmbeddingGeneratorClient(IEmbeddingGenerator<string, Embedding<float>> generator, string provider, string model, int dimensions)
    {
        _generator = generator;
        _provider = provider;
        _dimensions = dimensions;
        ModelId = $"{provider.ToLowerInvariant()}:{model}:{dimensions}";
    }

    public string ModelId { get; }

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct) => (await EmbedBatchAsync([text], ct))[0];

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];

        var embeddings = await ProviderErrors.Guard(_provider, () => _generator.GenerateAsync(texts, cancellationToken: ct), ct);
        if (embeddings.Count != texts.Count)
            throw new HttpRequestException($"{_provider} returned {embeddings.Count} embeddings for {texts.Count} inputs.");

        var vectors = embeddings.Select(e => e.Vector.ToArray()).ToList();
        if (vectors[0].Length != _dimensions)
            throw new InvalidOperationException(
                $"The {_provider} embedding model returns {vectors[0].Length}-dimensional vectors, but Rag:EmbeddingDimensions is " +
                $"{_dimensions}. Set Rag:EmbeddingDimensions to {vectors[0].Length} (and recalibrate Rag:MinScore for the new model).");
        return vectors;
    }
}
