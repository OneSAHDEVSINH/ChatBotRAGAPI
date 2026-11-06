using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatBotRAGAPI.Tests;

public sealed class FeedbackStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ragfeedback-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private FeedbackStore Store(int maxMb = 20) => new(Options.Create(new RagOptions { DataDirectory = _dir, FeedbackMaxFileMB = maxMb }), _time);

    [Fact]
    public void Entries_come_back_newest_first_with_totals_and_a_filter()
    {
        var store = Store();
        store.Add(new FeedbackRequest("When founded?", "2016 [1].", "up", ["profile.txt"]));
        _time.Advance(TimeSpan.FromMinutes(1));
        store.Add(new FeedbackRequest("Pro price?", "I don't have enough data.", "down", [], "It is in the catalog"));

        var all = store.List(null, 100);
        Assert.Equal((1, 1), (all.Up, all.Down));
        Assert.Equal(["Pro price?", "When founded?"], all.Entries.Select(e => e.Question));
        Assert.Equal("It is in the catalog", all.Entries[0].Comment);

        var down = Assert.Single(Store().List("down", 100).Entries); // a new store reads the same file
        Assert.Equal("down", down.Rating);
    }

    [Fact]
    public void A_full_log_refuses_new_ratings()
    {
        File.WriteAllBytes(Path.Combine(_dir, "feedback.jsonl"), new byte[1024 * 1024]);
        Assert.Null(Store(maxMb: 1).Add(new FeedbackRequest("q", "a", "up")));
    }

    [Fact]
    public void A_torn_last_line_is_skipped()
    {
        var store = Store();
        store.Add(new FeedbackRequest("q", "a", "up"));
        File.AppendAllText(Path.Combine(_dir, "feedback.jsonl"), "{\"id\":\"trunc");

        Assert.Single(store.List(null, 100).Entries);
    }
}

public sealed class FeedbackApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly RagApiFactory _factory = new(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "admin-key" });
    private readonly HttpClient _client;

    public FeedbackApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Anyone_can_rate_and_only_admins_can_read()
    {
        var rated = await _client.PostAsJsonAsync("/api/feedback", new FeedbackRequest("When founded?", "2016 [1].", "up", ["profile.txt"]), Ct);
        Assert.Equal(HttpStatusCode.Created, rated.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/feedback", Ct)).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/feedback?rating=up");
        request.Headers.Add(ApiKeyAttribute.HeaderName, "admin-key");
        var list = await (await _client.SendAsync(request, Ct)).Content.ReadFromJsonAsync<FeedbackList>(Ct);
        Assert.Equal(1, list!.Up);
        Assert.Equal(["profile.txt"], Assert.Single(list.Entries).Sources);
    }

    [Theory]
    [InlineData("sideways", "q", "a")]
    [InlineData("up", "", "a")]
    [InlineData("up", "q", "")]
    public async Task Bad_ratings_are_rejected(string rating, string question, string answer)
    {
        var response = await _client.PostAsJsonAsync("/api/feedback", new FeedbackRequest(question, answer, rating), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Oversized_fields_are_rejected()
    {
        var tooLong = new FeedbackRequest("q", "a", "down", Comment: new string('x', 1001));
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/feedback", tooLong, Ct)).StatusCode);

        var tooMany = new FeedbackRequest("q", "a", "up", Enumerable.Range(0, 21).Select(i => $"s{i}").ToList());
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/feedback", tooMany, Ct)).StatusCode);
    }
}
