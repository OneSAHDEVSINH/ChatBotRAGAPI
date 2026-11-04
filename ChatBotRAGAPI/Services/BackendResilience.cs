using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace ChatBotRAGAPI.Services;

public static class BackendResilience
{
    /// <summary>
    /// Retries transient failures of the Ollama/Qdrant clients: connection errors (backend restarting or still
    /// booting), 5xx, 408 and 429 (honouring Retry-After), with exponential backoff and jitter. Non-transient
    /// errors such as 404 "model not found" fail immediately.
    /// Deliberately not AddStandardResilienceHandler: its 10 s attempt / 30 s total timeouts would cut off CPU
    /// generations that take minutes; HttpClient.Timeout bounds each call instead.
    /// Every call made through these clients is safe to repeat (embed/generate/search, upsert and delete by id).
    /// </summary>
    public static IHttpClientBuilder AddBackendRetries(this IHttpClientBuilder builder, RagOptions options)
    {
        builder.AddResilienceHandler("backend-retry", pipeline =>
        {
            if (options.HttpRetryAttempts <= 0) return;

            pipeline.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = options.HttpRetryAttempts,
                Delay = TimeSpan.FromSeconds(Math.Max(0, options.HttpRetryBaseDelaySeconds)),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                ShouldRetryAfterHeader = true,
            });
        });
        return builder;
    }
}
