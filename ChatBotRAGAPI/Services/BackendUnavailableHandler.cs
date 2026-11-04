using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Turns a failed call to Ollama/Qdrant during a request (after retries) into 503 with the reason, instead of
/// an opaque 500. In the request path those are the only outbound HTTP calls (scraping runs in the worker).
/// </summary>
public sealed class BackendUnavailableHandler(IProblemDetailsService problemDetails, ILogger<BackendUnavailableHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is not HttpRequestException ex) return false;

        logger.LogError(ex, "AI/vector backend call failed for {Path}", context.Request.Path);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = ex,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "The AI or vector store backend is unavailable.",
                Detail = ex.Message,
            },
        });
    }
}
