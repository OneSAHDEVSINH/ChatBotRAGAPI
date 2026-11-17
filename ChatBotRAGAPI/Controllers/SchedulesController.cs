using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
[Route("api/schedules")]
public sealed class SchedulesController : ControllerBase
{
    private readonly CrawlScheduler _scheduler;
    private readonly KnowledgeBaseStore _knowledgeBases;

    public SchedulesController(CrawlScheduler scheduler, KnowledgeBaseStore knowledgeBases)
    {
        _scheduler = scheduler;
        _knowledgeBases = knowledgeBases;
    }

    /// <summary>Scheduled re-crawls, with when each runs next and how its last run went.</summary>
    /// <response code="200">The schedules, oldest first.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<CrawlScheduleInfo>> List() => Ok(_scheduler.List());

    /// <summary>Re-crawls a site every intervalHours, keeping its documents current.</summary>
    /// <remarks>The first run starts within a minute. With removeMissing, pages that disappear from the site are deleted on each run.</remarks>
    /// <response code="201">Created.</response>
    /// <response code="400">An invalid or private URL, maxPages or intervalHours out of range, or too many schedules.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<CrawlScheduleInfo> Create([FromBody] CrawlScheduleRequest request)
    {
        HttpContext.Items[AuditLog.TargetItem] = request.Url;
        var kb = KnowledgeBaseStore.Normalize(request.KnowledgeBase);
        if (!_knowledgeBases.Exists(kb)) return BadRequest($"Unknown knowledge base '{kb}'.");
        var (schedule, error) = _scheduler.Add(request);
        return error is not null ? BadRequest(error) : StatusCode(StatusCodes.Status201Created, schedule);
    }

    /// <summary>Stops a schedule. Documents it already ingested stay.</summary>
    /// <response code="204">Deleted.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="404">No such schedule.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(Guid id) => _scheduler.Remove(id) ? NoContent() : NotFound();

    /// <summary>Runs a schedule's crawl now instead of waiting for its next turn.</summary>
    /// <response code="202">Queued; returns the job id.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="404">No such schedule.</response>
    /// <response code="409">Its previous run is still queued or running.</response>
    [HttpPost("{id:guid}/run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult<EnqueueResponse> Run(Guid id) => _scheduler.RunNow(id) switch
    {
        null => NotFound(),
        { } job when job == Guid.Empty => Conflict("The previous run is still queued or running."),
        { } job => Accepted(new EnqueueResponse(job, "Queued")),
    };
}
