using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
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

    /// <summary>Ingests a public web page, or crawls a site starting from it (maxPages).</summary>
    /// <remarks>
    /// Runs in the background: poll GET /api/ingestion/{jobId} until Completed. A crawl follows links on the
    /// same host and under the same path as url, honours robots.txt, and stores each page as its own document;
    /// the job's message reports how many pages were ingested. Re-scraping a URL replaces that page's document;
    /// with removeMissing, pages of that site the crawl no longer finds are deleted.
    /// </remarks>
    /// <response code="202">Queued; returns the job id.</response>
    /// <response code="400">Not an http(s) URL, a private/loopback address, or maxPages out of range.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpPost("scrape")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<EnqueueResponse> Scrape([FromBody] ScrapeRequest request)
    {
        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) || !UrlSafety.IsAllowedScheme(uri))
            return BadRequest("Invalid URL. Only absolute http/https URLs are supported.");

        // Fast rejection for literal private IPs; hostnames are checked at connect time by the scraper's handler.
        if (System.Net.IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && !UrlSafety.IsPublicAddress(ip))
            return BadRequest("URLs pointing to private, loopback or link-local addresses are not allowed.");

        var maxPages = request.MaxPages ?? 1;
        if (maxPages < 1 || maxPages > _options.MaxCrawlPages)
            return BadRequest($"maxPages must be between 1 and {_options.MaxCrawlPages}.");
        if (request.RemoveMissing && maxPages == 1)
            return BadRequest("removeMissing needs a crawl: set maxPages above 1.");

        var jobId = _queue.Enqueue("url", request.Url, uri.ToString(), maxPages, request.RemoveMissing);
        return Accepted(new EnqueueResponse(jobId, "Queued"));
    }

    /// <summary>Uploads a .txt, .csv, .docx, .xlsx or .pdf file (up to 50 MB) for ingestion.</summary>
    /// <remarks>
    /// Runs in the background: poll GET /api/ingestion/{jobId} until Completed. The document is named by the
    /// file name, so uploading a file with the same name again replaces it. Scanned (image-only) PDFs fail.
    /// </remarks>
    /// <param name="file">The file (multipart form field "file").</param>
    /// <param name="ct">Request cancellation.</param>
    /// <response code="202">Queued; returns the job id.</response>
    /// <response code="400">Empty file or unsupported type.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpPost("upload")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [RequestSizeLimit(50_000_000)]
    public async Task<ActionResult<EnqueueResponse>> Upload(IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest("Empty file.");

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!FileTextExtractor.SupportedExtensions.Contains(ext))
            return BadRequest($"Supported file types: {string.Join(", ", FileTextExtractor.SupportedExtensions)}.");

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

        // The saved name is a GUID; keep the client's file name (sans any path) for citations.
        var jobId = _queue.Enqueue("file", savedPath, Path.GetFileName(file.FileName));
        return Accepted(new EnqueueResponse(jobId, "Queued"));
    }

    /// <summary>Status of an ingestion job: Queued, Running, Completed or Failed (with a message).</summary>
    /// <remarks>Statuses are kept in memory: they are gone after a restart and expire an hour after the job finishes.</remarks>
    /// <response code="200">The job's status.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="404">Unknown or expired job id.</response>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<JobStatusResponse> Status(Guid jobId)
    {
        var status = _queue.GetStatus(jobId);
        if (status.Status == "NotFound") return NotFound();

        return Ok(new JobStatusResponse(jobId, status.Status, status.Message));
    }
}