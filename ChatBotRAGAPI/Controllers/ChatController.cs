using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChatBotRAGAPI.Controllers;

[ApiController]
[Route("api/chat")]
public sealed class ChatController : ControllerBase
{
    private readonly RagService _ragService;

    public ChatController(RagService ragService) => _ragService = ragService;

    [HttpPost("ask")]
    public async Task<ActionResult<AskResponse>> Ask([FromBody] AskRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Question))
            return BadRequest("Question is required.");

        var result = await _ragService.AskAsync(request.Question, ct);
        return Ok(result);
    }
}