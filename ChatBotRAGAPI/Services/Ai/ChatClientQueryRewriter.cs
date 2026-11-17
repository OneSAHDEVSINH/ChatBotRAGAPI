using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatMessage = ChatBotRAGAPI.Contracts.ChatMessage;

namespace ChatBotRAGAPI.Services.Ai;

/// <summary>
/// Follow-up rewriting through a Microsoft.Extensions.AI chat client (OpenAI-compatible or Anthropic). Uses the
/// provider's normal output budget: on reasoning models the hidden reasoning counts against it.
/// </summary>
public sealed class ChatClientQueryRewriter(IChatClient chat, string provider, int maxOutputTokens) : IQueryRewriter
{
    private readonly ChatOptions _options = new() { MaxOutputTokens = maxOutputTokens };

    public async Task<string?> RewriteAsync(string question, IReadOnlyList<ChatMessage> history, CancellationToken ct)
    {
        var response = await ProviderErrors.Guard(provider,
            () => chat.GetResponseAsync([new AiChatMessage(ChatRole.User, QueryRewritePrompt.Build(question, history))], _options, ct), ct);
        return QueryRewritePrompt.Clean(response.Text);
    }
}
