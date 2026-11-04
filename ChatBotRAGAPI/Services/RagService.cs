using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

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

    public async Task<AskResponse> AskAsync(string question, CancellationToken ct)
    {
        var qvec = await _embeddingClient.EmbedAsync(question, ct);
        var hits = await _vectorStore.SearchAsync(qvec, _options.TopK, ct);

        // Weak matches mislead the generator more than they help; drop them. If nothing clears the bar,
        // answer directly instead of paying for an LLM call that can only hallucinate.
        var minScore = _options.EffectiveMinScore;
        var relevant = hits.Where(h => h.Score >= minScore).ToList();
        if (relevant.Count == 0)
            return new AskResponse(NoDataAnswer, [], []);

        var answer = await _answerGenerator.GenerateAnswerAsync(question, relevant, ct);

        return new AskResponse(
            answer,
            relevant.Select(h => h.Text).ToList(),
            relevant.Select((h, i) => new ContextSource(i + 1, h.Source, Math.Round(h.Score, 4))).ToList());
    }
}