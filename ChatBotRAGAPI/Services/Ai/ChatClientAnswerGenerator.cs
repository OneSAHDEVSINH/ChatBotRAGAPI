using ChatBotRAGAPI.Contracts;
using Microsoft.Extensions.AI;
using System.Runtime.CompilerServices;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = ChatBotRAGAPI.Contracts.ChatMessage;

namespace ChatBotRAGAPI.Services.Ai;

/// <summary>
/// Answers through any Microsoft.Extensions.AI <see cref="IChatClient"/> (OpenAI, Azure OpenAI, OpenAI-compatible
/// servers, Anthropic), with the same prompt as the Ollama path. No sampling settings are sent: current Claude
/// models reject them and the Anthropic adapter drops them, and the grounding rules do the steering.
/// </summary>
public sealed class ChatClientAnswerGenerator : IAnswerGenerator
{
    /// <summary>Returned when the provider declined to answer (a safety refusal) and produced no text.</summary>
    public const string DeclinedAnswer = "The model declined to answer this question.";

    private readonly IChatClient _chat;
    private readonly string _provider;
    private readonly ChatOptions _options;

    public ChatClientAnswerGenerator(IChatClient chat, string provider, int maxOutputTokens)
    {
        _chat = chat;
        _provider = provider;
        _options = new ChatOptions { MaxOutputTokens = maxOutputTokens };
    }

    private static List<AiChatMessage> Messages(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history) =>
        [new AiChatMessage(ChatRole.User, AnswerPrompt.Build(question, context, history))];

    public async Task<string> GenerateAnswerAsync(
        string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var response = await ProviderErrors.Guard(_provider,
            () => _chat.GetResponseAsync(Messages(question, context, history), _options, ct), ct);

        var answer = AnswerPrompt.StripThinking(response.Text ?? "");
        return answer.Length == 0 && response.FinishReason == ChatFinishReason.ContentFilter ? DeclinedAnswer : answer;
    }

    public async IAsyncEnumerable<string> StreamAnswerAsync(
        string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var filter = new ThinkTagFilter();
        var wrote = false;
        ChatFinishReason? finish = null;

        await using var updates = _chat.GetStreamingResponseAsync(Messages(question, context, history), _options, ct).GetAsyncEnumerator(ct);
        while (await ProviderErrors.Guard(_provider, () => updates.MoveNextAsync().AsTask(), ct))
        {
            finish ??= updates.Current.FinishReason;
            var text = filter.Push(updates.Current.Text ?? "");
            if (text.Length == 0) continue;
            wrote = true;
            yield return text;
        }

        var rest = filter.Flush();
        if (rest.Length > 0) { wrote = true; yield return rest; }
        if (!wrote && finish == ChatFinishReason.ContentFilter) yield return DeclinedAnswer;
    }
}
