namespace ChatBotRAGAPI.Services;

public interface IAnswerGenerator
{
    Task<string> GenerateAnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct);
}