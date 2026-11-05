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
    private readonly ChatActivity _activity;
    private readonly ILogger<ChatController> _logger;

    public ChatController(RagService ragService, ChatActivity activity, ILogger<ChatController> logger)
    {
        _ragService = ragService;
        _activity = activity;
        _logger = logger;
    }

    /// <summary>
    /// Ingestion yields to chat while this request runs. Released when the response completes, so a streamed
    /// answer counts until its last event, and an aborted request is released too.
    /// </summary>
    private void MarkChatActive() => HttpContext.Response.RegisterForDispose(_activity.Begin());

    /// <summary>Answers a question from the ingested content, with [n] citations matching sources[].id.</summary>
    /// <remarks>
    /// Answers "I don't have enough data." when nothing relevant was ingested. For a follow-up question, send the
    /// earlier turns as history (the server keeps no conversation state); leave history out for a first question.
    /// </remarks>
    /// <response code="200">The answer, the passages it was based on, and their sources.</response>
    /// <response code="400">Empty question, or invalid history.</response>
    /// <response code="429">Too many requests from this client; retry shortly.</response>
    /// <response code="503">The embedding/chat backend (Ollama) or vector store is unavailable.</response>
    [HttpPost("ask")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        MarkChatActive();

        var result = await _ragService.AskAsync(request.Question, request.History, ct);
        return Ok(result);
    }

    /// <summary>Same as ask, but streams the answer as Server-Sent Events while it is generated.</summary>
    /// <remarks>
    /// Events: "sources" (the cited passages, sent before generation starts), "context" (their texts, in the same
    /// order, like AskResponse.context), then "delta" pieces of the answer,
    /// then "done" with the full answer, or "error" if generation fails. Swagger UI only shows the stream once it
    /// has finished; use curl -N or an EventSource-style client to see it live.
    /// </remarks>
    /// <response code="200">An event stream.</response>
    /// <response code="400">Empty question, or invalid history.</response>
    /// <response code="503">The backend is unavailable (before streaming starts).</response>
    [HttpPost("ask/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [Produces("text/event-stream")]
    public async Task<IResult> AskStream([FromBody] AskRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return Results.BadRequest(error);
        MarkChatActive();

        var answer = await _ragService.AskStreamingAsync(request.Question, request.History, ct);
        return TypedResults.ServerSentEvents(Events(answer, ct));
    }

    private async IAsyncEnumerable<SseItem<object>> Events(
        RagService.StreamingAnswer answer, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return new SseItem<object>(answer.Sources, "sources");
        yield return new SseItem<object>(answer.Context, "context"); // passage texts, same order as sources

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
