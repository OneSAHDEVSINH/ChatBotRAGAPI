using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[Route("api/ingestion")]
public sealed class IngestionController : ControllerBase
{
    private readonly IngestionQueue _queue;
    private readonly RagOptions _options;

    public IngestionController(IngestionQueue queue, IOptions<RagOptions> options)
    {
        _queue = queue;
        _options = options.Value;
    }

    [HttpPost("scrape")]
    public ActionResult<EnqueueResponse> Scrape([FromBody] ScrapeRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !UrlSafety.IsAllowedScheme(uri))
            return BadRequest("Invalid URL. Only absolute http/https URLs are supported.");

        // Fast rejection for literal private IPs; hostnames are checked at connect time by the scraper's handler.
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !UrlSafety.IsPublicAddress(ip))
            return BadRequest("URLs pointing to private, loopback or link-local addresses are not allowed.");

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

        var inbox = Path.Combine(_options.DataDirectory, "inbox");
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