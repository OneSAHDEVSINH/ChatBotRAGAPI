using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

public interface IAnswerGenerator
{
    /// <summary>
    /// Answers from the given hits only. Hit i (0-based) is cited as [i+1], matching <see cref="ContextSource.Id"/>.
    /// Callers guarantee <paramref name="context"/> is non-empty. <paramref name="history"/> holds earlier turns
    /// (oldest first) for resolving references in the question; it is never a source of facts.
    /// </summary>
    Task<string> GenerateAnswerAsync(
        string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct);

    /// <summary>Same as <see cref="GenerateAnswerAsync"/>, yielding the answer in pieces as it is generated.</summary>
    IAsyncEnumerable<string> StreamAnswerAsync(
        string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct);
}
