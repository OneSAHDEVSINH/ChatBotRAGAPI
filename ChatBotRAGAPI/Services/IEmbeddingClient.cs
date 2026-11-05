namespace ChatBotRAGAPI.Services;

public interface IEmbeddingClient
{
    /// <summary>
    /// Identifies the vector space (model and size). Cached embeddings are reused only when this matches,
    /// so changing the embedding model re-embeds everything instead of mixing incompatible vectors.
    /// </summary>
    string ModelId => GetType().Name;

    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    /// <summary>Embeds several texts in one call; results are in input order.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
