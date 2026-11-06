using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Decorates the real vector store so every upsert/delete also updates the <see cref="KeywordIndex"/>;
/// callers (worker, documents controller) don't need to know hybrid search exists.
/// The keyword index is updated only after the inner store succeeds, so it never holds chunks the store lacks.
/// </summary>
public sealed class KeywordIndexingVectorStore(IVectorStore inner, KeywordIndex keywords) : IVectorStore
{
    public bool IsPersistent => inner.IsPersistent;

    public Task EnsureCollectionAsync(CancellationToken ct) => inner.EnsureCollectionAsync(ct);

    public Task PingAsync(CancellationToken ct) => inner.PingAsync(ct);

    public async Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        await inner.UpsertAsync(points, ct);
        foreach (var p in points) keywords.Upsert(p.Id, p.Text, p.Source);
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        await inner.DeleteAsync(ids, ct);
        keywords.Remove(ids);
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct) =>
        inner.SearchAsync(vector, topK, ct);
}
