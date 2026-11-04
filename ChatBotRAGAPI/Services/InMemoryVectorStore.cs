using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

public sealed class InMemoryVectorStore : IVectorStore
{
    private sealed record Entry(Guid Id, float[] Vector, string Text, string Source);

    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly object _sync = new();

    public Task EnsureCollectionAsync(CancellationToken ct) => Task.CompletedTask;

    public Task UpsertAsync(IReadOnlyList<VectorPoint> points, CancellationToken ct)
    {
        lock (_sync)
        {
            // Same semantics as Qdrant: an existing id is replaced, not duplicated.
            foreach (var p in points)
                _entries[p.Id] = new Entry(p.Id, p.Vector, p.Text, p.Source);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] vector, int topK, CancellationToken ct)
    {
        List<SearchHit> result;
        lock (_sync)
        {
            result = _entries.Values
                .Select(e => new SearchHit(e.Text, e.Source, Cosine(vector, e.Vector)))
                .OrderByDescending(x => x.Score)
                .Take(topK)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<SearchHit>>(result);
    }

    private static double Cosine(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        if (len == 0) return 0;

        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < len; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}