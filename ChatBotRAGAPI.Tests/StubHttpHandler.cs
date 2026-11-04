using System.Net;
using System.Text;

namespace ChatBotRAGAPI.Tests;

/// <summary>Answers every request via a delegate and records request bodies.</summary>
internal sealed class StubHttpHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string? Body)> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string html, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        Requests.Add((request, body));
        return respond(request, body);
    }
}
