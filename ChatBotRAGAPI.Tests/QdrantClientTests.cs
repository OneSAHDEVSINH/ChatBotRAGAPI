using System.Net;
using System.Text.Json;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class QdrantClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Delete_posts_point_ids_to_the_collection()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json("""{"result":{"status":"completed"}}"""));
        var client = new QdrantClient(new HttpClient(handler),
            Options.Create(new RagOptions { QdrantBaseUrl = "http://qdrant.test:6333", QdrantCollection = "kb" }));
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };

        await client.DeleteAsync(ids, Ct);
        await client.DeleteAsync([], Ct); // no request for nothing

        var (request, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://qdrant.test:6333/collections/kb/points/delete?wait=true", request.RequestUri!.ToString());
        using var json = JsonDocument.Parse(body!);
        Assert.Equal(ids.Select(i => i.ToString()), json.RootElement.GetProperty("points").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Failed_delete_throws()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json("{}", HttpStatusCode.InternalServerError));
        var client = new QdrantClient(new HttpClient(handler), Options.Create(new RagOptions()));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeleteAsync([Guid.NewGuid()], Ct));
    }
}
