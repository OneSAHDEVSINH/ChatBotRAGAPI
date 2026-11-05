using Anthropic;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.AI;
using OpenAI;
using System.ClientModel;

namespace ChatBotRAGAPI.Services.Ai;

/// <summary>
/// Chooses the answering, embedding and OCR backends from Rag:ChatProvider / Rag:EmbeddingProvider. Ollama keeps
/// its own client (think=false, keep_alive, num_ctx, retries); OpenAI-compatible servers and Anthropic go through
/// Microsoft.Extensions.AI, registered as <see cref="IChatClient"/> / <see cref="IEmbeddingGenerator{TInput, TEmbedding}"/>
/// and adapted to the app's <see cref="IAnswerGenerator"/>, <see cref="IEmbeddingClient"/> and <see cref="IImageTextReader"/>.
/// Misconfiguration fails at startup with the setting to fix.
/// </summary>
public static class AiProviders
{
    public const string Ollama = "Ollama";
    public const string OpenAI = "OpenAI";
    public const string Anthropic = "Anthropic";

    private static readonly string[] ChatProviders = [Ollama, OpenAI, Anthropic];
    private static readonly string[] EmbeddingProviders = [Ollama, OpenAI];

    public static void Register(IServiceCollection services, RagOptions options)
    {
        var chat = Normalize(options.ChatProvider, ChatProviders, "Rag:ChatProvider");
        var embedding = Normalize(options.EmbeddingProvider, EmbeddingProviders, "Rag:EmbeddingProvider",
            options.EmbeddingProvider.Equals(Anthropic, StringComparison.OrdinalIgnoreCase)
                ? "Anthropic has no embeddings API; use Ollama or OpenAI for Rag:EmbeddingProvider." : null);
        var ocr = !string.IsNullOrWhiteSpace(options.OcrModel);

        if (chat == Ollama || embedding == Ollama)
        {
            services.AddHttpClient<OllamaClient>(client =>
            {
                client.BaseAddress = new Uri(options.OllamaBaseUrl);
                client.Timeout = TimeSpan.FromMinutes(10); // CPU generations are slow
            }).AddBackendRetries(options);
        }

        // Answers
        switch (chat)
        {
            case Ollama:
                services.AddSingleton<IAnswerGenerator>(sp => sp.GetRequiredService<OllamaClient>());
                if (ocr) services.AddSingleton<IImageTextReader>(sp => sp.GetRequiredService<OllamaClient>());
                break;

            case OpenAI:
                var openAi = OpenAiClient(options.OpenAI);
                services.AddChatClient(openAi.GetChatClient(options.ChatModel).AsIChatClient()).UseLogging();
                services.AddSingleton<IAnswerGenerator>(sp =>
                    new ChatClientAnswerGenerator(sp.GetRequiredService<IChatClient>(), OpenAI, options.OpenAI.MaxOutputTokens));
                if (ocr) services.AddSingleton<IImageTextReader>(new ChatClientImageTextReader(
                    openAi.GetChatClient(options.OcrModel).AsIChatClient(), OpenAI, options.OcrMaxTokens));
                break;

            case Anthropic:
                var claude = AnthropicClient(options.Anthropic);
                services.AddChatClient(claude.AsIChatClient(options.ChatModel)).UseLogging();
                services.AddSingleton<IAnswerGenerator>(sp =>
                    new ChatClientAnswerGenerator(sp.GetRequiredService<IChatClient>(), Anthropic, options.Anthropic.MaxTokens));
                if (ocr) services.AddSingleton<IImageTextReader>(new ChatClientImageTextReader(
                    claude.AsIChatClient(options.OcrModel), Anthropic, options.OcrMaxTokens));
                break;
        }

        // Embeddings
        switch (embedding)
        {
            case Ollama:
                services.AddSingleton<IEmbeddingClient>(sp => sp.GetRequiredService<OllamaClient>());
                break;

            case OpenAI:
                services.AddEmbeddingGenerator(OpenAiClient(options.OpenAI).GetEmbeddingClient(options.EmbeddingModel).AsIEmbeddingGenerator());
                services.AddSingleton<IEmbeddingClient>(sp => new EmbeddingGeneratorClient(
                    sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(), OpenAI, options.EmbeddingModel, options.EmbeddingDimensions));
                break;
        }
    }

    private static string Normalize(string value, string[] allowed, string setting, string? special = null)
    {
        var match = allowed.FirstOrDefault(a => a.Equals(value?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException(
            special ?? $"{setting} is '{value}'; use one of: {string.Join(", ", allowed)}.");
    }

    private static OpenAIClient OpenAiClient(OpenAiProviderOptions options)
    {
        var key = string.IsNullOrWhiteSpace(options.ApiKey) ? Environment.GetEnvironmentVariable("OPENAI_API_KEY") : options.ApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                "The OpenAI provider needs an API key: set Rag__OpenAI__ApiKey or OPENAI_API_KEY (any non-empty value for a local OpenAI-compatible server).");

        var clientOptions = new OpenAIClientOptions();
        if (!string.IsNullOrWhiteSpace(options.Endpoint)) clientOptions.Endpoint = new Uri(options.Endpoint);
        return new OpenAIClient(new ApiKeyCredential(key), clientOptions);
    }

    /// <summary>Without a key the SDK finds credentials itself (ANTHROPIC_API_KEY, then an `ant auth login` profile).</summary>
    private static AnthropicClient AnthropicClient(AnthropicProviderOptions options) =>
        string.IsNullOrWhiteSpace(options.ApiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = options.ApiKey };
}
