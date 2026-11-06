using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[Route("api/feedback")]
public sealed class FeedbackController : ControllerBase
{
    private const int MaxQuestion = 4000, MaxAnswer = 20000, MaxComment = 1000, MaxSources = 20, MaxSourceLength = 2048;
    private readonly FeedbackStore _store;

    public FeedbackController(FeedbackStore store) => _store = store;

    /// <summary>Rates an answer as helpful or not (sent by the chat UI's buttons).</summary>
    /// <remarks>Open to end users, like chat. Ratings are appended to data/feedback.jsonl; read them with GET /api/feedback.</remarks>
    /// <response code="201">Stored.</response>
    /// <response code="400">Rating is not "up" or "down", or a field is empty or too long.</response>
    /// <response code="503">The feedback log has reached Rag:FeedbackMaxFileMB.</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public ActionResult Rate([FromBody] FeedbackRequest request)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        return _store.Add(request) is { } entry
            ? StatusCode(StatusCodes.Status201Created, new { entry.Id })
            : Problem("The feedback log is full (Rag:FeedbackMaxFileMB).", statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    /// <summary>Recent ratings, newest first, with totals of helpful and not-helpful.</summary>
    /// <param name="rating">"up" or "down" to filter; omit for both.</param>
    /// <param name="limit">How many entries (1 to 500, default 100).</param>
    /// <response code="200">Totals and entries.</response>
    /// <response code="400">An unknown rating filter.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet]
    [ApiKey]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<FeedbackList> List([FromQuery] string? rating = null, [FromQuery] int limit = 100)
    {
        if (rating is not (null or "up" or "down")) return BadRequest("rating must be \"up\" or \"down\".");
        return Ok(_store.List(rating, limit));
    }

    private static string? Validate(FeedbackRequest r)
    {
        if (r.Rating is not ("up" or "down")) return "rating must be \"up\" or \"down\".";
        if (string.IsNullOrWhiteSpace(r.Question) || r.Question.Length > MaxQuestion) return $"question is required (at most {MaxQuestion} characters).";
        if (string.IsNullOrWhiteSpace(r.Answer) || r.Answer.Length > MaxAnswer) return $"answer is required (at most {MaxAnswer} characters).";
        if (r.Comment is { Length: > MaxComment }) return $"comment is at most {MaxComment} characters.";
        if (r.Sources is { } s && (s.Count > MaxSources || s.Any(x => string.IsNullOrEmpty(x) || x.Length > MaxSourceLength)))
            return $"sources: at most {MaxSources}, each non-empty and at most {MaxSourceLength} characters.";
        return null;
    }
}
