using Microsoft.Extensions.AI;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace ChatBotRAGAPI.Services.Ai;

/// <summary>OCR for scanned PDF pages through a vision-capable <see cref="IChatClient"/> bound to Rag:OcrModel.</summary>
public sealed class ChatClientImageTextReader : IImageTextReader
{
    private readonly IChatClient _chat;
    private readonly string _provider;
    private readonly ChatOptions _options;

    public ChatClientImageTextReader(IChatClient chat, string provider, int maxOutputTokens)
    {
        _chat = chat;
        _provider = provider;
        _options = new ChatOptions { MaxOutputTokens = maxOutputTokens };
    }

    public async Task<string> ReadTextAsync(byte[] image, CancellationToken ct)
    {
        var mediaType = image.Length > 2 && image[0] == 0xFF && image[1] == 0xD8 ? "image/jpeg" : "image/png";
        var message = new AiChatMessage(ChatRole.User, [new TextContent(OcrPrompt.Text), new DataContent(image, mediaType)]);

        var response = await ProviderErrors.Guard(_provider, () => _chat.GetResponseAsync([message], _options, ct), ct);
        return AnswerPrompt.StripThinking(response.Text ?? "");
    }
}
