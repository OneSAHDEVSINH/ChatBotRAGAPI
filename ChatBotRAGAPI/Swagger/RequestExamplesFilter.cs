using ChatBotRAGAPI.Contracts;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Text.Json.Nodes;

namespace ChatBotRAGAPI.Swagger;

/// <summary>
/// Gives JSON request bodies realistic, selectable examples in Swagger UI instead of the generated
/// placeholders ({"question":"string","history":[{"role":"string","content":"string"}]}), which Swagger
/// pre-fills and which would otherwise be sent as a real conversation turn. The first example is the one
/// pre-filled; the others are in the "Examples" drop-down.
/// </summary>
public sealed class RequestExamplesFilter : IOperationFilter
{
    private static readonly Dictionary<Type, (string Name, string Summary, string Json)[]> Examples = new()
    {
        [typeof(AskRequest)] =
        [
            ("question", "A question", """{ "question": "When was the company founded?" }"""),
            ("followUp", "A follow-up, with the earlier turns as history", """
                {
                  "question": "And where is the head office?",
                  "history": [
                    { "role": "user", "content": "When was the company founded?" },
                    { "role": "assistant", "content": "The company was founded in 2016 [1]." }
                  ]
                }
                """),
        ],
        [typeof(ScrapeRequest)] =
        [
            ("page", "One page", """{ "url": "https://example.com/" }"""),
            ("crawl", "Crawl the site from this page (up to 20 pages)", """{ "url": "https://example.com/", "maxPages": 20 }"""),
            ("recrawl", "Re-crawl and remove pages that are gone", """{ "url": "https://example.com/", "maxPages": 20, "removeMissing": true }"""),
        ],
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var bodyType = context.ApiDescription.ParameterDescriptions
            .FirstOrDefault(p => p.Source.Id == "Body")?.Type;
        if (bodyType is null || !Examples.TryGetValue(bodyType, out var examples)) return;
        if (operation.RequestBody?.Content is not { } content) return;

        foreach (var mediaType in content.Values)
        {
            mediaType.Examples = examples.ToDictionary(
                e => e.Name,
                e => (IOpenApiExample)new OpenApiExample { Summary = e.Summary, Value = JsonNode.Parse(e.Json) });
        }
    }
}
