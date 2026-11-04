using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[Route("api/ingestion")]
public sealed class IngestionController : ControllerBase
{
    private readonly IngestionQueue _queue;
    private readonly IWebHostEnvironment _env;

    public IngestionController(IngestionQueue queue, IWebHostEnvironment env)
    {
        _queue = queue;
        _env = env;
    }

    [HttpPost("scrape")]
    public ActionResult<EnqueueResponse> Scrape([FromBody] ScrapeRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out _))
            return BadRequest("Invalid URL.");

        var jobId = _queue.Enqueue("url", request.Url);
        return Accepted(new EnqueueResponse(jobId, "Queued"));
    }

    [HttpPost("upload")]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<EnqueueResponse>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("Empty file.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".txt" or ".csv" or ".docx" or ".xlsx"))
            return BadRequest("Only txt/csv/docx/xlsx are supported.");

        var inbox = Path.Combine(_env.ContentRootPath, "data", "inbox");
        Directory.CreateDirectory(inbox);

        var savedPath = Path.Combine(inbox, $"{Guid.NewGuid()}{ext}");

        await using (var stream = new FileStream(
            savedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            options: FileOptions.Asynchronous))
        {
            await file.CopyToAsync(stream, ct);
            await stream.FlushAsync(ct);
        } // fully closed here

        var jobId = _queue.Enqueue("file", savedPath);
        return Accepted(new EnqueueResponse(jobId, "Queued"));
    }

    [HttpGet("{jobId:guid}")]
    public ActionResult<JobStatusResponse> Status(Guid jobId)
    {
        var status = _queue.GetStatus(jobId);
        if (status.Status == "NotFound") return NotFound();

        return Ok(new JobStatusResponse(jobId, status.Status, status.Message));
    }
}