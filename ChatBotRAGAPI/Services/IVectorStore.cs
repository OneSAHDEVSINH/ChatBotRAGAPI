using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

public interface IVectorStore
{
    Task EnsureCollectionAsync(CancellationToken ct);
    Task UpsertAsync(Guid id, float[] vector, string text, string source, CancellationToken ct);
    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct);
}