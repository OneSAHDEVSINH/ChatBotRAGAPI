namespace ChatBotRAGAPI.Services;

public interface IEmbeddingClient
{
    Task<float[]> EmbedAsync(string text, CancellationToken ct);
}