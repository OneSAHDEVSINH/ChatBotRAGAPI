namespace ChatBotRAGAPI.Services;

/// <summary>
/// In-memory BM25 index over chunk texts, the keyword half of hybrid search. Embeddings capture meaning but can
/// rank exact terms (plan names, codes, "Q3-2026") low; BM25 rewards rare query terms that occur verbatim.
/// Tokenized with <see cref="LocalText"/> (lower-case alphanumerics, stopwords dropped), then stemmed with
/// <see cref="PorterStemmer"/> so word forms meet ("founded"/"found", "costs"/"cost").
/// Kept in sync with the vector store by <see cref="KeywordIndexingVectorStore"/>.
/// </summary>
public sealed class KeywordIndex
{
    private const double K1 = 1.2;
    private const double B = 0.75;

    /// <param name="Coverage">Share of the query's distinct terms found in the chunk (0..1).</param>
    public sealed record Hit(Guid Id, string Text, string Source, double Score, double Coverage);

    private sealed record Doc(string Text, string Source, Dictionary<string, int> TermCounts, int Length);

    private readonly Dictionary<Guid, Doc> _docs = [];
    private readonly Dictionary<string, HashSet<Guid>> _postings = new(StringComparer.Ordinal);
    private long _totalLength;
    private readonly object _sync = new();

    public int Count
    {
        get { lock (_sync) return _docs.Count; }
    }

    public void Upsert(Guid id, string text, string source)
    {
        var counts = Terms(text).GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        lock (_sync)
        {
            RemoveLocked(id);
            var doc = new Doc(text, source, counts, counts.Values.Sum());
            _docs[id] = doc;
            _totalLength += doc.Length;
            foreach (var term in counts.Keys)
            {
                if (!_postings.TryGetValue(term, out var ids)) _postings[term] = ids = [];
                ids.Add(id);
            }
        }
    }

    public void Remove(IEnumerable<Guid> ids)
    {
        lock (_sync)
        {
            foreach (var id in ids) RemoveLocked(id);
        }
    }

    public IReadOnlyList<Hit> Search(string query, int topK)
    {
        var terms = Terms(query).Distinct(StringComparer.Ordinal).ToList();
        if (terms.Count == 0 || topK <= 0) return [];

        lock (_sync)
        {
            if (_docs.Count == 0) return [];

            var avgLength = Math.Max(1.0, (double)_totalLength / _docs.Count);
            var scores = new Dictionary<Guid, (double Score, int Matched)>();

            foreach (var term in terms)
            {
                if (!_postings.TryGetValue(term, out var ids)) continue;
                var idf = Math.Log(1 + (_docs.Count - ids.Count + 0.5) / (ids.Count + 0.5));

                foreach (var id in ids)
                {
                    var doc = _docs[id];
                    var tf = doc.TermCounts[term];
                    var termScore = idf * tf * (K1 + 1) / (tf + K1 * (1 - B + B * doc.Length / avgLength));
                    scores[id] = scores.TryGetValue(id, out var s) ? (s.Score + termScore, s.Matched + 1) : (termScore, 1);
                }
            }

            return scores
                .OrderByDescending(kv => kv.Value.Score)
                .Take(topK)
                .Select(kv => new Hit(kv.Key, _docs[kv.Key].Text, _docs[kv.Key].Source, kv.Value.Score, (double)kv.Value.Matched / terms.Count))
                .ToList();
        }
    }

    /// <summary>Index terms: tokens stemmed, so a query and a passage match across word forms.</summary>
    public static IEnumerable<string> Terms(string text) => LocalText.Tokenize(text).Select(PorterStemmer.Stem);

    private void RemoveLocked(Guid id)
    {
        if (!_docs.Remove(id, out var doc)) return;
        _totalLength -= doc.Length;
        foreach (var term in doc.TermCounts.Keys)
        {
            if (_postings.TryGetValue(term, out var ids) && ids.Remove(id) && ids.Count == 0)
                _postings.Remove(term);
        }
    }
}
