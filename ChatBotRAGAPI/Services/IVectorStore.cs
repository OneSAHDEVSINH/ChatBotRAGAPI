using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

public interface IVectorStore
{
    Task EnsureCollectionAsync(CancellationToken ct);

    /// <summary>Inserts or replaces points by id (an existing id must be overwritten, not duplicated).</summary>
    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct);
}
