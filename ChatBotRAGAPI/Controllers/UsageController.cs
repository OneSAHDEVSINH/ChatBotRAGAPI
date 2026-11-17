using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
[Route("api/usage")]
public sealed class UsageController(UsageLog usage) : ControllerBase
{
    /// <summary>Questions per day, how they ended, ratings, answer time, and the most asked unanswered questions.</summary>
    /// <param name="days">Days back from today (UTC), 1 to 365.</param>
    /// <response code="200">The report.</response>
    /// <response code="400">days out of range.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<UsageReport> Get([FromQuery] int days = 30) =>
        days is < 1 or > 365 ? BadRequest("days must be between 1 and 365.") : Ok(usage.Report(days));
}
