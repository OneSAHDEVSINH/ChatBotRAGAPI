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

    public async Task<AskResponse> AskAsync(string question, CancellationToken ct)
    {
        var qvec = await _embeddingClient.EmbedAsync(question, ct);
        var hits = await _vectorStore.SearchAsync(qvec, _options.TopK, ct);

        var context = hits.Select(h => h.Text).ToArray();
        var answer = await _answerGenerator.GenerateAnswerAsync(question, context, ct);

        return new AskResponse(answer, context);
    }
}