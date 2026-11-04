using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace ChatBotRAGAPI.Services;

public sealed class RagService
{
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IAnswerGenerator _answerGenerator;
    private readonly IVectorStore _vectorStore;
    private readonly RagOptions _options;

    public RagService(
        IEmbeddingClient embeddingClient,
        IAnswerGenerator answerGenerator,
        IVectorStore vectorStore,
        IOptions<RagOptions> options)
    {
        _embeddingClient = embeddingClient;
        _answerGenerator = answerGenerator;
        _vectorStore = vectorStore;
        _options = options.Value;
    }

    public const string NoDataAnswer = "I don't have enough data.";

    /// <summary>Retrieved passages for a question, plus an answer that is produced when enumerated.</summary>
    public sealed record StreamingAnswer(
        IReadOnlyList<string> Context, IReadOnlyList<ContextSource> Sources, IAsyncEnumerable<string> Pieces);

    public async Task<AskResponse> AskAsync(string question, IReadOnlyList<ChatMessage>? history, CancellationToken ct)
    {
        var turns = RecentHistory(history);
        var relevant = await RetrieveAsync(question, turns, ct);

        // If nothing clears the bar, answer directly instead of paying for an LLM call that can only hallucinate.
        var answer = relevant.Count == 0
            ? NoDataAnswer
            : await _answerGenerator.GenerateAnswerAsync(question, relevant, turns, ct);

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
            : _answerGenerator.StreamAnswerAsync(question, relevant, turns, ct);

        return new StreamingAnswer(Texts(relevant), Sources(relevant), pieces);
    }

    /// <summary>
    /// Text that is embedded for search. A follow-up ("and the Enterprise plan?") rarely names its topic, so the
    /// previous user question is searched together with it.
    /// </summary>
    public static string RetrievalQuery(string question, IReadOnlyList<ChatMessage> history)
    {
        var previous = history.LastOrDefault(m => m.Role == "user")?.Content;
        return string.IsNullOrWhiteSpace(previous) ? question : $"{previous}\n{question}";
    }

    private async Task<List<SearchHit>> RetrieveAsync(string question, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var qvec = await _embeddingClient.EmbedAsync(RetrievalQuery(question, history), ct);
        var hits = await _vectorStore.SearchAsync(qvec, _options.TopK, ct);

        // Weak matches mislead the generator more than they help; drop them.
        var minScore = _options.EffectiveMinScore;
        return hits.Where(h => h.Score >= minScore).ToList();
    }

    private IReadOnlyList<ChatMessage> RecentHistory(IReadOnlyList<ChatMessage>? history) =>
        history is null or { Count: 0 } ? [] : history.TakeLast(Math.Max(0, _options.MaxHistoryMessages)).ToList();

    private static List<string> Texts(List<SearchHit> hits) => hits.Select(h => h.Text).ToList();

    private static List<ContextSource> Sources(List<SearchHit> hits) =>
        hits.Select((h, i) => new ContextSource(i + 1, h.Source, Math.Round(h.Score, 4))).ToList();

    private static async IAsyncEnumerable<string> Single(string text)
    {
        await Task.CompletedTask;
        yield return text;
    }
}
