using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
    private const int MaxHistoryContentLength = 8000;

    private readonly RagService _ragService;
    private readonly ILogger<ChatController> _logger;

    public ChatController(RagService ragService, ILogger<ChatController> logger)
    {
        _ragService = ragService;
        _logger = logger;
    }

    [HttpPost("ask")]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return BadRequest(error);

        var result = await _ragService.AskAsync(request.Question, request.History, ct);
        return Ok(result);
    }

    /// <summary>
    /// Server-Sent Events: "sources" (the cited passages, sent before generation starts), then "delta" pieces
    /// of the answer as they are generated, then "done" with the full answer, or "error" if generation fails.
    /// </summary>
    [HttpPost("ask/stream")]
    [Produces("text/event-stream")]
    public async Task<IResult> AskStream([FromBody] AskRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return Results.BadRequest(error);

        var answer = await _ragService.AskStreamingAsync(request.Question, request.History, ct);
        return TypedResults.ServerSentEvents(Events(answer, ct));
    }

    private async IAsyncEnumerable<SseItem<object>> Events(
        RagService.StreamingAnswer answer, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SseItem<object>(answer.Sources, "sources");

        var full = new StringBuilder();
        await using var pieces = answer.Pieces.GetAsyncEnumerator(ct);
        while (true)
        {
            // Headers are already sent, so a failure can't become an HTTP status; report it as an event instead.
            // (C# doesn't allow yield inside try/catch, hence the manual enumeration.)
            string? failure = null;
            try
            {
                if (!await pieces.MoveNextAsync()) break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogError(ex, "Answer generation failed while streaming");
                failure = ex.Message;
            }

            if (failure is not null)
            {
                yield return new SseItem<object>(new StreamError(failure), "error");
                yield break;
            }

            full.Append(pieces.Current);
            yield return new SseItem<object>(new StreamDelta(pieces.Current), "delta");
        }

        yield return new SseItem<object>(new StreamDone(full.ToString().Trim()), "done");
    }

    private static string? Validate(AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return "Question is required.";

        foreach (var message in request.History ?? [])
        {
            if (message.Role is not ("user" or "assistant"))
                return "History roles must be \"user\" or \"assistant\".";
            if (string.IsNullOrWhiteSpace(message.Content) || message.Content.Length > MaxHistoryContentLength)
                return $"History messages must be non-empty and at most {MaxHistoryContentLength} characters.";
        }

        return null;
    }
}
