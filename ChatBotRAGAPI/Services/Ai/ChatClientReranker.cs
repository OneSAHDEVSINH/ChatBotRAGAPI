using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace ChatBotRAGAPI.Services.Ai;

/// <summary>Reranking through a Microsoft.Extensions.AI chat client (OpenAI-compatible or Anthropic).</summary>
public sealed class ChatClientReranker(IChatClient chat, string provider, int maxOutputTokens) : IReranker
{
    private readonly ChatOptions _options = new() { MaxOutputTokens = maxOutputTokens };

    public async Task<IReadOnlyList<int>> RankAsync(string question, IReadOnlyList<string> passages, CancellationToken ct)
    {
        var response = await ProviderErrors.Guard(provider,
            () => chat.GetResponseAsync([new AiChatMessage(ChatRole.User, RerankPrompt.Build(question, passages))], _options, ct), ct);
        return RerankPrompt.Parse(response.Text, passages.Count);
    }
}
