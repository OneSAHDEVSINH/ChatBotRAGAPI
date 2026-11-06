using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
[Route("api/eval")]
public sealed class EvalController : ControllerBase
{
    private const int MaxCases = 100;
    private readonly EvalService _eval;

    public EvalController(EvalService eval) => _eval = eval;

    /// <summary>Suggests Rag:MinScore for the current embedding model from known questions.</summary>
    /// <remarks>
    /// Give answerable questions (with expectedSource) and off-topic ones (without). Only vector search is measured, and
    /// no answers are generated, so it is quick. Run it after changing the embedding model, with its documents ingested.
    /// ChatBotRAGAPI/testdata/eval.json fits the sample files.
    /// </remarks>
    /// <response code="200">Per-question scores, the current threshold's results and a suggestion when the groups separate.</response>
    /// <response code="400">No cases, more than 100, or an empty question.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpPost("calibrate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<CalibrationReport>> Calibrate([FromBody] EvalRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        return Ok(await _eval.CalibrateAsync(request.Cases, ct));
    }

    /// <summary>Asks each question for real and checks retrieval, facts, citations and refusals.</summary>
    /// <remarks>
    /// Each case is a full answer from the configured model (10 to 60 s each on a CPU-only Ollama), run one after another,
    /// so keep the client's timeout long. A case passes when the expected source was retrieved and cited, every
    /// mustContain fact is in the answer and no [n] points past the passages; an off-topic case passes when refused.
    /// </remarks>
    /// <response code="200">A summary and the result of every case.</response>
    /// <response code="400">No cases, more than 100, or an empty question.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="503">The model backend is unavailable.</response>
    [HttpPost("answers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<AnswerEvalReport>> Answers([FromBody] EvalRequest request, CancellationToken ct)
    {
        if (Validate(request) is { } error) return BadRequest(error);
        return Ok(await _eval.EvaluateAnswersAsync(request.Cases, ct));
    }

    private static string? Validate(EvalRequest request)
    {
        if (request.Cases is not { Count: > 0 }) return "Give at least one case.";
        if (request.Cases.Count > MaxCases) return $"At most {MaxCases} cases per run.";
        if (request.Cases.Any(c => string.IsNullOrWhiteSpace(c.Question))) return "Every case needs a question.";
        return null;
    }
}
