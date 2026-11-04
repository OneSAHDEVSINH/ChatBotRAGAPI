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

    /// <summary>All ingested documents. A document's source is its original file name or URL.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<DocumentInfo>> List() =>
        Ok(_registry.List().Select(e => e.ToInfo()).ToList());

    /// <summary>Removes a document and all of its chunks. Source is passed as a query parameter because URLs contain slashes.</summary>
    [HttpDelete]
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
