using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[ApiKey]
[Route("api/knowledge-bases")]
public sealed class KnowledgeBasesController(KnowledgeBaseStore store, DocumentRegistry registry) : ControllerBase
{
    private KnowledgeBaseInfo Info(string id, string name, bool isPrivate) => new(id, name, isPrivate, registry.Count(id));

    /// <summary>All knowledge bases, "default" first, with whether they are private and how many documents they hold.</summary>
    /// <response code="200">The knowledge bases.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<IReadOnlyList<KnowledgeBaseInfo>> List() =>
        Ok(store.List().Select(b => Info(b.Id, b.Name, b.Private)).ToList());

    /// <summary>Creates a knowledge base. A private one returns its chat key once: store it, it cannot be shown again.</summary>
    /// <remarks>Ingest into it with knowledgeBase on upload/scrape/schedules; chat with it via knowledgeBase in the ask body (?kb= in the chat UI, data-knowledge-base on the widget).</remarks>
    /// <response code="201">Created.</response>
    /// <response code="400">Invalid or duplicate id, or too many knowledge bases.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public ActionResult<KnowledgeBaseResponse> Create([FromBody] KnowledgeBaseRequest request)
    {
        HttpContext.Items[AuditLog.TargetItem] = request.Id;
        var (key, error) = store.Create(request.Id, request.Name, request.Private);
        if (error is not null) return BadRequest(error);
        var created = store.List().Single(b => b.Id == request.Id);
        return StatusCode(StatusCodes.Status201Created, new KnowledgeBaseResponse(Info(created.Id, created.Name, created.Private), key));
    }

    /// <summary>Renames a knowledge base, makes it public or private, or issues a new chat key.</summary>
    /// <response code="200">Updated; chatKey is set when a new key was issued.</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="404">No such knowledge base.</response>
    [HttpPatch("{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<KnowledgeBaseResponse> Update(string id, [FromBody] KnowledgeBaseUpdate request)
    {
        var (found, key) = store.Update(id, request.Name, request.Private, request.NewKey);
        if (!found) return NotFound();
        var updated = store.List().Single(b => b.Id == id);
        return Ok(new KnowledgeBaseResponse(Info(updated.Id, updated.Name, updated.Private), key));
    }

    /// <summary>Deletes an empty knowledge base. Delete its documents (and schedules) first; "default" cannot be deleted.</summary>
    /// <response code="204">Deleted.</response>
    /// <response code="400">It still has documents, or it is "default".</response>
    /// <response code="401">Missing or wrong X-Api-Key (when Security:ApiKey is set).</response>
    /// <response code="404">No such knowledge base.</response>
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(string id, CancellationToken ct)
    {
        if (id == KnowledgeBaseStore.Default) return BadRequest("The default knowledge base cannot be deleted.");
        if (!store.Exists(id)) return NotFound();
        using (await registry.LockAsync(ct))
        {
            var count = registry.Count(id);
            if (count > 0) return BadRequest($"'{id}' still has {count} document(s); delete them first.");
            store.Remove(id);
        }
        return NoContent();
    }
}
