using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

public interface IVectorStore
{
    /// <summary>False when points are lost on restart (in-memory); the worker then re-indexes stored documents at startup.</summary>
    bool IsPersistent { get; }

    Task EnsureCollectionAsync(CancellationToken ct);

    /// <summary>Throws when the store cannot serve requests right now (readiness check). In-process stores always can.</summary>
    Task PingAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Inserts or replaces points by id (an existing id must be overwritten, not duplicated).</summary>
    Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct);

    /// <summary>Removes points by id; unknown ids are ignored.</summary>
    Task DeleteAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct);

    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct);
}
