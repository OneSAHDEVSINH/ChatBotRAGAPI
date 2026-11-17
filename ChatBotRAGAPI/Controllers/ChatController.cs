using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
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
    private readonly UsageLog _usage;
    private readonly KnowledgeBaseStore _knowledgeBases;
    private readonly ILogger<ChatController> _logger;

    public ChatController(RagService ragService, ChatActivity activity, UsageLog usage, KnowledgeBaseStore knowledgeBases,
        ILogger<ChatController> logger)
    {
        _ragService = ragService;
        _activity = activity;
        _usage = usage;
        _knowledgeBases = knowledgeBases;
        _logger = logger;
    }

    /// <summary>
    /// 404 for an unknown knowledge base, 401 for a private one without its X-Chat-Key. The management API key also
    /// opens private ones, so staff can test them from Swagger or the admin page.
    /// </summary>
    private ActionResult? CheckAccess(string kb)
    {
        var access = _knowledgeBases.CanChat(kb, Request.Headers[KnowledgeBaseStore.ChatKeyHeader].ToString());
        if (access == KnowledgeBaseStore.Access.KeyRequired && Security.ApiKeyAttribute.IsValidManagementKey(HttpContext))
            access = KnowledgeBaseStore.Access.Allowed;
        return access switch
        {
            KnowledgeBaseStore.Access.NotFound => NotFound($"Unknown knowledge base '{kb}'."),
            KnowledgeBaseStore.Access.KeyRequired => Unauthorized($"The knowledge base '{kb}' is private: send its key in the {KnowledgeBaseStore.ChatKeyHeader} header."),
            _ => null,
        };
    }

    private static bool IsFollowUp(AskRequest request) => request.History?.Any(m => m.Role == "user") == true;

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
    /// <response code="401">The knowledge base is private and X-Chat-Key is missing or wrong.</response>
    /// <response code="404">Unknown knowledge base.</response>
    /// <response code="429">Too many requests from this client; retry shortly.</response>
    /// <response code="503">The embedding/chat backend (Ollama) or vector store is unavailable.</response>
    [HttpPost("ask")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        var kb = KnowledgeBaseStore.Normalize(request.KnowledgeBase);
        if (CheckAccess(kb) is { } denied) return denied;
        MarkChatActive();

        var watch = Stopwatch.StartNew();
        AskResponse result;
        try
        {
            result = await _ragService.AskAsync(request.Question, request.History, kb, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _usage.Record(request.Question, UsageLog.Error, 0, watch.Elapsed, IsFollowUp(request), kb);
            throw;
        }

        _usage.Record(request.Question, UsageLog.OutcomeOf(result.Answer), result.Sources.Count, watch.Elapsed, IsFollowUp(request), kb);
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
    /// <response code="401">The knowledge base is private and X-Chat-Key is missing or wrong.</response>
    /// <response code="404">Unknown knowledge base.</response>
    /// <response code="503">The backend is unavailable (before streaming starts).</response>
    [HttpPost("ask/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    [Produces("text/event-stream")]
    public async Task<IResult> AskStream([FromBody] AskRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return Results.BadRequest(error);
        var kb = KnowledgeBaseStore.Normalize(request.KnowledgeBase);
        switch (CheckAccess(kb))
        {
            case NotFoundObjectResult notFound: return Results.NotFound(notFound.Value);
            case UnauthorizedObjectResult denied: return Results.Json(denied.Value, statusCode: StatusCodes.Status401Unauthorized);
        }
        MarkChatActive();

        var watch = Stopwatch.StartNew();
        RagService.StreamingAnswer answer;
        try
        {
            answer = await _ragService.AskStreamingAsync(request.Question, request.History, kb, ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _usage.Record(request.Question, UsageLog.Error, 0, watch.Elapsed, IsFollowUp(request), kb);
            throw;
        }
        return TypedResults.ServerSentEvents(Events(answer, request, kb, watch, ct));
    }

    private async IAsyncEnumerable<SseItem<object>> Events(
        RagService.StreamingAnswer answer, AskRequest request, string kb, Stopwatch watch, [EnumeratorCancellation] CancellationToken ct)
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
                _usage.Record(request.Question, UsageLog.Error, answer.Sources.Count, watch.Elapsed, IsFollowUp(request), kb);
                yield return new SseItem<object>(new StreamError(failure), "error");
                yield break;
            }

            full.Append(pieces.Current);
            yield return new SseItem<object>(new StreamDelta(pieces.Current), "delta");
        }

        var text = full.ToString().Trim();
        _usage.Record(request.Question, UsageLog.OutcomeOf(text), answer.Sources.Count, watch.Elapsed, IsFollowUp(request), kb);
        yield return new SseItem<object>(new StreamDone(text), "done");
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
