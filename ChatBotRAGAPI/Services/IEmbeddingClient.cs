namespace ChatBotRAGAPI.Services;

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    /// <summary>Embeds several texts in one call; results are in input order.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
