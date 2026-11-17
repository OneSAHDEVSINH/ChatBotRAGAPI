using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
[Route("api")]
public sealed class AdminDataController(BackupService backups, AuditLog audit) : ControllerBase
{
    /// <summary>Downloads a backup: every document (texts and cached embeddings), schedules, ratings, usage and audit logs.</summary>
    /// <remarks>Restore it here or on another server with POST /api/backup/restore. The download itself is audited.</remarks>
    /// <response code="200">A zip file.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet("backup")]
    [AuditRead]
    [Produces("application/zip")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Backup(CancellationToken ct)
    {
        var (zip, fileName) = await backups.CreateAsync(ct);
        HttpContext.Items[AuditLog.TargetItem] = fileName;
        return File(zip, "application/zip", fileName);
    }

    /// <summary>Replaces all documents, schedules, ratings and usage with a backup's, then re-indexes every document.</summary>
    /// <remarks>
    /// Everything ingested since the backup is lost. Refused while ingestion jobs are running. The re-index runs as
    /// ordinary "reindex" jobs; follow them in GET /api/ingestion/jobs. The audit log is kept as it is.
    /// </remarks>
    /// <response code="200">Restored; re-index jobs queued.</response>
    /// <response code="400">Not a valid backup of this app.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="409">Ingestion jobs are queued or running.</response>
    [HttpPost("backup/restore")]
    [RequestSizeLimit(2_000_000_000)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_000_000_000)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<RestoreResponse>> Restore(IFormFile file, CancellationToken ct)
    {
        HttpContext.Items[AuditLog.TargetItem] = file?.FileName;
        if (file is null || file.Length == 0) return BadRequest("Upload the backup .zip as \"file\".");

        await using var stream = file.OpenReadStream();
        var (result, error, busy) = await backups.RestoreAsync(stream, ct);
        if (busy) return Conflict(error);
        return error is not null ? BadRequest(error) : Ok(result);
    }

    /// <summary>Recent management actions, newest first: who, from where, what, on what, and the result.</summary>
    /// <param name="limit">1 to 1000.</param>
    /// <response code="200">The entries.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet("audit")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<AuditEntry>> Audit([FromQuery] int limit = 200) => Ok(audit.List(limit));
}
