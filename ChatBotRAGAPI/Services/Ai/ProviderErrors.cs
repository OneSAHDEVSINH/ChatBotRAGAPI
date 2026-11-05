namespace ChatBotRAGAPI.Services.Ai;

/// <summary>
/// Provider SDKs throw their own exception types (OpenAI's ClientResultException, Anthropic's AnthropicApiException,
/// ...). Wrapping them as <see cref="HttpRequestException"/> keeps one failure path: a chat request answers 503 with
/// the provider's message (BackendUnavailableHandler), and an ingestion job fails with it. Cancellation passes through.
/// </summary>
public static class ProviderErrors
{
    public static async Task<T> Guard<T>(string provider, Func<Task<T>> call, CancellationToken ct)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or HttpRequestException) || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // A timeout surfaces as a cancellation the caller did not ask for: that is a backend failure too.
            throw new HttpRequestException($"{provider} request failed: {ex.Message}", ex);
        }
    }
}
