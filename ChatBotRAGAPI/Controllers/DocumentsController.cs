using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
[Route("api/documents")]
public sealed class DocumentsController : ControllerBase
{
    private readonly DocumentRegistry _registry;
    private readonly IVectorStore _vectorStore;

    public DocumentsController(DocumentRegistry registry, IVectorStore vectorStore)
    {
        _registry = registry;
        _vectorStore = vectorStore;
    }

    /// <summary>Lists all ingested documents. A document's source is its original file name or URL.</summary>
    /// <response code="200">The documents, with their chunk counts and ingestion times.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<DocumentInfo>> List() =>
        Ok(_registry.List().Select(e => e.ToInfo()).ToList());

    /// <summary>Removes a document and all of its chunks.</summary>
    /// <param name="source">The document's source exactly as listed: a file name or a full URL.</param>
    /// <param name="ct">Request cancellation.</param>
    /// <response code="204">Removed.</response>
    /// <response code="400">source is missing.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="404">No document has that source.</response>
    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromQuery] string source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source)) return BadRequest("source is required.");

        using (await _registry.LockAsync(ct))
        {
            var entry = _registry.Get(source);
            if (entry is null) return NotFound();

            await _vectorStore.DeleteAsync(entry.ChunkIds.ToList(), ct);
            _registry.Remove(source);
        }

        return NoContent();
    }
}
