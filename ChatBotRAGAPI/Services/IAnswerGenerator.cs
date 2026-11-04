using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

public interface IAnswerGenerator
{
    /// <summary>
    /// Answers from the given hits only. Hit i (0-based) is cited as [i+1], matching <see cref="ContextSource.Id"/>.
    /// Callers guarantee <paramref name="context"/> is non-empty.
    /// </summary>
    Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, CancellationToken ct);
}
