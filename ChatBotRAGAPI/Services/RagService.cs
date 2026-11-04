using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

public sealed class RagService
{
    /// <summary>Reciprocal rank fusion constant; 60 is the value from the original RRF paper and common practice.</summary>
    private const int RrfK = 60;

    private readonly IEmbeddingClient _embeddingClient;
    private readonly IAnswerGenerator _answerGenerator;
    private readonly IVectorStore _vectorStore;
    private readonly KeywordIndex? _keywords;
    private readonly RagOptions _options;

    public RagService(
        IEmbeddingClient embeddingClient,
        IAnswerGenerator answerGenerator,
        IVectorStore vectorStore,
        IOptions<RagOptions> options,
        KeywordIndex? keywords = null)
    {
        _embeddingClient = embeddingClient;
        _answerGenerator = answerGenerator;
        _vectorStore = vectorStore;
        _keywords = keywords;
        _options = options.Value;
    }

    public const string NoDataAnswer = "I don't have enough data.";

    /// <summary>Retrieved passages for a question, plus an answer that is produced when enumerated.</summary>
    public sealed record StreamingAnswer(
        IReadOnlyList<string> Context, IReadOnlyList<ContextSource> Sources, IAsyncEnumerable<string> Pieces);

    /// <summary>A retrieved passage with how it was found; Cosine is null for keyword-only matches.</summary>
    private sealed record Retrieved(SearchHit Hit, double? Cosine, string Match);

    public async Task<AskResponse> AskAsync(string question, IReadOnlyList<ChatMessage>? history, CancellationToken ct)
    {
        var turns = RecentHistory(history);
        var relevant = await RetrieveAsync(question, turns, ct);

        // If nothing clears the bar, answer directly instead of paying for an LLM call that can only hallucinate.
        var answer = relevant.Count == 0
            ? NoDataAnswer
            : await _answerGenerator.GenerateAnswerAsync(question, Hits(relevant), turns, ct);

        return new AskResponse(answer, Texts(relevant), Sources(relevant));
    }

    /// <summary>
    /// Retrieval runs before this returns, so its failures (e.g. embedding backend down) surface as a normal
    /// HTTP error; only generation happens while the response is streaming.
    /// </summary>
    public async Task<StreamingAnswer> AskStreamingAsync(string question, IReadOnlyList<ChatMessage>? history, CancellationToken ct)
    {
        var turns = RecentHistory(history);
        var relevant = await RetrieveAsync(question, turns, ct);

        var pieces = relevant.Count == 0
            ? Single(NoDataAnswer)
            : _answerGenerator.StreamAnswerAsync(question, Hits(relevant), turns, ct);

        return new StreamingAnswer(Texts(relevant), Sources(relevant), pieces);
    }

    /// <summary>
    /// Text that is searched for. A follow-up ("and the Enterprise plan?") rarely names its topic, so the
    /// previous user question is searched together with it.
    /// </summary>
    public static string RetrievalQuery(string question, IReadOnlyList<ChatMessage> history)
    {
        var previous = history.LastOrDefault(m => m.Role == "user")?.Content;
        return string.IsNullOrWhiteSpace(previous) ? question : $"{previous}\n{question}";
    }

    /// <summary>
    /// Vector search, optionally fused with BM25 keyword search. A passage is admitted when its cosine clears
    /// EffectiveMinScore (weak semantic matches mislead the generator) or when it contains at least
    /// MinKeywordCoverage of the query terms. Admitted passages are ordered by reciprocal rank fusion of their
    /// positions in each candidate list, so they need no common score scale.
    /// </summary>
    private async Task<List<Retrieved>> RetrieveAsync(string question, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var query = RetrievalQuery(question, history);
        var minScore = _options.EffectiveMinScore;
        var hybrid = _options.HybridSearch && _keywords is not null;
        var pool = hybrid ? Math.Max(_options.TopK, _options.SearchCandidates) : _options.TopK;

        var qvec = await _embeddingClient.EmbedAsync(query, ct);
        var vectorHits = await _vectorStore.SearchAsync(qvec, pool, ct);

        if (!hybrid)
        {
            return vectorHits
                .Where(h => h.Score >= minScore)
                .Select(h => new Retrieved(h, h.Score, "semantic"))
                .ToList();
        }

        var keywordHits = _keywords!.Search(query, pool);

        // Same chunk text from the same source = same passage (chunk ids are derived from exactly these).
        var fused = new Dictionary<(string Source, string Text), (SearchHit Hit, double? Cosine, bool Semantic, bool Keyword, double Rrf)>();

        for (var rank = 0; rank < vectorHits.Count; rank++)
        {
            var h = vectorHits[rank];
            var admitted = h.Score >= minScore;
            fused[(h.Source, h.Text)] = (h, h.Score, admitted, false, admitted ? 1.0 / (RrfK + rank + 1) : 0);
        }

        for (var rank = 0; rank < keywordHits.Count; rank++)
        {
            var k = keywordHits[rank];
            var key = (k.Source, k.Text);
            var contribution = 1.0 / (RrfK + rank + 1);

            // Already admitted by meaning: any keyword match is extra evidence and lifts its rank.
            if (fused.TryGetValue(key, out var e) && e.Semantic)
            {
                fused[key] = (e.Hit, e.Cosine, true, true, e.Rrf + contribution);
                continue;
            }

            // Keyword-only: must cover enough of the query on its own.
            if (k.Coverage < _options.MinKeywordCoverage) continue;
            fused[key] = fused.TryGetValue(key, out e)
                ? (e.Hit, e.Cosine, false, true, contribution)
                : (new SearchHit(k.Text, k.Source, 0), null, false, true, contribution);
        }

        return fused.Values
            .Where(e => e.Semantic || e.Keyword)
            .OrderByDescending(e => e.Rrf)
            .ThenByDescending(e => e.Cosine ?? 0)
            .Take(_options.TopK)
            .Select(e => new Retrieved(e.Hit, e.Cosine, e.Semantic && e.Keyword ? "both" : e.Semantic ? "semantic" : "keyword"))
            .ToList();
    }

    private IReadOnlyList<ChatMessage> RecentHistory(IReadOnlyList<ChatMessage>? history) =>
        history is null or { Count: 0 } ? [] : history.TakeLast(Math.Max(0, _options.MaxHistoryMessages)).ToList();

    private static List<SearchHit> Hits(List<Retrieved> retrieved) => retrieved.Select(r => r.Hit).ToList();

    private static List<string> Texts(List<Retrieved> retrieved) => retrieved.Select(r => r.Hit.Text).ToList();

    private static List<ContextSource> Sources(List<Retrieved> retrieved) =>
        retrieved.Select((r, i) => new ContextSource(
            i + 1, r.Hit.Source, r.Cosine is { } c ? Math.Round(c, 4) : null, r.Match)).ToList();

    private static async IAsyncEnumerable<string> Single(string text)
    {
        await Task.CompletedTask;
        yield return text;
    }
}
