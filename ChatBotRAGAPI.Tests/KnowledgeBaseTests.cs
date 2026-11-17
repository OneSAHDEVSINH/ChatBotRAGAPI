using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public sealed class KnowledgeBaseStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ragkb-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private KnowledgeBaseStore Store() => new(Options.Create(new RagOptions { DataDirectory = _dir }));

    [Fact]
    public void Default_always_exists_and_is_public()
    {
        var store = Store();
        Assert.True(store.Exists("default"));
        Assert.Equal(KnowledgeBaseStore.Access.Allowed, store.CanChat("default", null));
        Assert.False(store.Remove("default"));
        Assert.Equal(KnowledgeBaseStore.Access.NotFound, store.CanChat("nope", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Internal")]
    [InlineData("-x")]
    [InlineData("has space")]
    [InlineData("a/b")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 41
    public void Ids_are_slugs(string id) => Assert.NotNull(Store().Create(id, null, false).Error);

    [Fact]
    public void A_private_knowledge_base_needs_its_key_which_survives_a_restart()
    {
        var (key, error) = Store().Create("internal", "Staff handbook", isPrivate: true);
        Assert.Null(error);
        Assert.False(string.IsNullOrEmpty(key));

        var store = Store(); // reloaded from disk
        Assert.Equal(KnowledgeBaseStore.Access.KeyRequired, store.CanChat("internal", null));
        Assert.Equal(KnowledgeBaseStore.Access.KeyRequired, store.CanChat("internal", key + "x"));
        Assert.Equal(KnowledgeBaseStore.Access.Allowed, store.CanChat("internal", key));
        Assert.DoesNotContain(key!, File.ReadAllText(Path.Combine(_dir, "knowledge-bases.json"))); // only its hash is kept
        Assert.Contains(store.List(), b => b is { Id: "internal", Name: "Staff handbook", Private: true });
        Assert.NotNull(store.Create("internal", null, false).Error); // duplicate
    }

    [Fact]
    public void Keys_can_be_replaced_and_knowledge_bases_made_public()
    {
        var store = Store();
        var (first, _) = store.Create("internal", null, isPrivate: true);

        var (found, second) = store.Update("internal", null, null, newKey: true);
        Assert.True(found);
        Assert.Equal(KnowledgeBaseStore.Access.KeyRequired, store.CanChat("internal", first));
        Assert.Equal(KnowledgeBaseStore.Access.Allowed, store.CanChat("internal", second));

        Assert.Null(store.Update("internal", "Renamed", false, false).ChatKey);
        Assert.Equal(KnowledgeBaseStore.Access.Allowed, store.CanChat("internal", null));

        var (_, third) = store.Update("internal", null, true, false); // private again: a fresh key
        Assert.NotNull(third);
        Assert.Equal(KnowledgeBaseStore.Access.KeyRequired, store.CanChat("internal", second));
        Assert.False(store.Update("missing", null, true, false).Found);
    }
}

public sealed class KnowledgeBaseApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly RagApiFactory _factory = new(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "admin" });
    private readonly HttpClient _admin;
    private readonly HttpClient _visitor;

    public KnowledgeBaseApiTests()
    {
        _admin = _factory.CreateClient();
        _admin.DefaultRequestHeaders.Add("X-Api-Key", "admin");
        _visitor = _factory.CreateClient();
    }

    public void Dispose()
    {
        _admin.Dispose();
        _visitor.Dispose();
        _factory.Dispose();
    }

    private async Task<string?> CreateAsync(string id, bool isPrivate)
    {
        var response = await _admin.PostAsJsonAsync("/api/knowledge-bases", new KnowledgeBaseRequest(id, null, isPrivate), Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KnowledgeBaseResponse>(Ct))!.ChatKey;
    }

    private async Task<HttpResponseMessage> UploadAsync(string fileName, string content, string? kb)
    {
        using var form = new MultipartFormDataContent { { new StringContent(content), "file", fileName } };
        if (kb is not null) form.Add(new StringContent(kb), "knowledgeBase");
        return await _admin.PostAsync("/api/ingestion/upload", form, Ct);
    }

    private async Task IngestAsync(string fileName, string content, string? kb)
    {
        var response = await UploadAsync(fileName, content, kb);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var job = (await response.Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
        for (var i = 0; i < 200; i++)
        {
            var status = await _admin.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{job}", Ct);
            if (status!.Status == "Completed") return;
            Assert.NotEqual("Failed", status.Status);
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException();
    }

    private async Task<HttpResponseMessage> AskAsync(HttpClient client, string question, string? kb, string? chatKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat/ask") { Content = JsonContent.Create(new AskRequest(question, null, kb)) };
        if (chatKey is not null) request.Headers.Add("X-Chat-Key", chatKey);
        return await client.SendAsync(request, Ct);
    }

    private async Task<string> AnswerAsync(HttpClient client, string question, string? kb, string? chatKey = null)
    {
        var response = await AskAsync(client, question, kb, chatKey);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AskResponse>(Ct))!.Answer;
    }

    [Fact]
    public async Task Each_knowledge_base_answers_only_from_its_own_documents()
    {
        await CreateAsync("shop", isPrivate: false);
        await IngestAsync("warranty.txt", "Warranty: all devices carry a two year limited warranty.", null);
        await IngestAsync("returns.txt", "Returns: unopened items can be returned within 60 days.", "shop");

        Assert.Contains("two year", await AnswerAsync(_visitor, "How long is the warranty?", null));
        Assert.Equal(RagService.NoDataAnswer, await AnswerAsync(_visitor, "How long is the warranty?", "shop"));
        Assert.Contains("60 days", await AnswerAsync(_visitor, "Can unopened items be returned?", "shop"));
        Assert.Equal(RagService.NoDataAnswer, await AnswerAsync(_visitor, "Can unopened items be returned?", "default"));

        var shopDocs = await _admin.GetFromJsonAsync<List<DocumentInfo>>("/api/documents?knowledgeBase=shop", Ct);
        Assert.Equal(["returns.txt"], shopDocs!.Select(d => d.Source));
        var all = await _admin.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct);
        Assert.Equal([("default", "warranty.txt"), ("shop", "returns.txt")], all!.Select(d => (d.KnowledgeBase, d.Source)));
    }

    [Fact]
    public async Task The_same_source_in_two_knowledge_bases_is_two_documents()
    {
        await CreateAsync("shop", isPrivate: false);
        await IngestAsync("policy.txt", "Warranty: all devices carry a two year limited warranty.", null);
        await IngestAsync("policy.txt", "Warranty: all devices carry a two year limited warranty.", "shop");

        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync("/api/documents?source=policy.txt&knowledgeBase=shop", Ct)).StatusCode);

        Assert.Contains("two year", await AnswerAsync(_visitor, "How long is the warranty?", null)); // default's copy is untouched
        Assert.Equal(RagService.NoDataAnswer, await AnswerAsync(_visitor, "How long is the warranty?", "shop"));
    }

    [Fact]
    public async Task A_private_knowledge_base_needs_its_chat_key_or_the_management_key()
    {
        var key = await CreateAsync("internal", isPrivate: true);
        Assert.NotNull(key);
        await IngestAsync("handbook.txt", "Holidays: staff get 24 days of paid leave per year.", "internal");

        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(_visitor, "How many days of paid leave?", "internal")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await AskAsync(_visitor, "How many days of paid leave?", "internal", "wrong")).StatusCode);
        Assert.Contains("24 days", await AnswerAsync(_visitor, "How many days of paid leave?", "internal", key));
        Assert.Contains("24 days", await AnswerAsync(_admin, "How many days of paid leave?", "internal"));

        using var stream = new HttpRequestMessage(HttpMethod.Post, "/api/chat/ask/stream")
        { Content = JsonContent.Create(new AskRequest("How many days of paid leave?", null, "internal")) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await _visitor.SendAsync(stream, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_knowledge_bases_are_refused_everywhere()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(_visitor, "q", "missing")).StatusCode);
        using var stream = new HttpRequestMessage(HttpMethod.Post, "/api/chat/ask/stream") { Content = JsonContent.Create(new AskRequest("q", null, "missing")) };
        Assert.Equal(HttpStatusCode.NotFound, (await _visitor.SendAsync(stream, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync("a.txt", "x", "missing")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/ingestion/scrape",
            new ScrapeRequest("https://example.com/", null, false, "missing"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.PostAsJsonAsync("/api/schedules",
            new CrawlScheduleRequest("https://example.com/", 5, true, 24, "missing"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Only_an_empty_knowledge_base_can_be_deleted()
    {
        await CreateAsync("shop", isPrivate: false);
        await IngestAsync("returns.txt", "Returns: within 60 days.", "shop");

        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.DeleteAsync("/api/knowledge-bases/shop", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _admin.DeleteAsync("/api/knowledge-bases/default", Ct)).StatusCode);
        await _admin.DeleteAsync("/api/documents?source=returns.txt&knowledgeBase=shop", Ct);
        Assert.Equal(HttpStatusCode.NoContent, (await _admin.DeleteAsync("/api/knowledge-bases/shop", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await AskAsync(_visitor, "q", "shop")).StatusCode);

        var list = await _admin.GetFromJsonAsync<List<KnowledgeBaseInfo>>("/api/knowledge-bases", Ct);
        Assert.Equal(["default"], list!.Select(b => b.Id));
    }

    [Fact]
    public async Task Knowledge_bases_need_the_management_key()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _visitor.GetAsync("/api/knowledge-bases", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _visitor.PostAsJsonAsync("/api/knowledge-bases", new KnowledgeBaseRequest("x"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Usage_can_be_filtered_by_knowledge_base()
    {
        await CreateAsync("shop", isPrivate: false);
        await AskAsync(_visitor, "Anything about boats?", "shop");
        await AskAsync(_visitor, "Anything about cars?", null);

        var shop = await _admin.GetFromJsonAsync<UsageReport>("/api/usage?days=1&knowledgeBase=shop", Ct);
        var all = await _admin.GetFromJsonAsync<UsageReport>("/api/usage?days=1", Ct);

        Assert.Equal(["Anything about boats?"], shop!.Unanswered.Select(q => q.Question));
        Assert.Equal(2, all!.Questions);
    }
}

public sealed class KnowledgeBaseStorageTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Default_keeps_its_old_file_names_and_chunk_ids_so_existing_data_needs_no_migration()
    {
        var dir = Directory.CreateTempSubdirectory("ragkbcompat-").FullName;
        try
        {
            // An index.json written before knowledge bases: no KnowledgeBase field.
            var docs = Directory.CreateDirectory(Path.Combine(dir, "documents")).FullName;
            File.WriteAllText(Path.Combine(docs, "index.json"),
                """[{"Source":"a.txt","SourceType":"file","IngestedAt":"2026-01-01T00:00:00+00:00","ChunkIds":[]}]""");

            var registry = new DocumentRegistry(Options.Create(new RagOptions { DataDirectory = dir }), NullLogger<DocumentRegistry>.Instance);

            var entry = Assert.Single(registry.List("default"));
            Assert.Equal("default", entry.KnowledgeBase);
            var oldId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("a.txt")).AsSpan(0, 16));
            Assert.Equal(Path.Combine(docs, $"{oldId}.txt"), registry.TextPath("default", "a.txt"));
            Assert.NotEqual(registry.TextPath("default", "a.txt"), registry.TextPath("shop", "a.txt"));

            var oldChunk = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes("a.txt\0hello")).AsSpan(0, 16));
            Assert.Equal(oldChunk, IngestionWorker.ChunkId("default", "a.txt", "hello"));
            Assert.NotEqual(oldChunk, IngestionWorker.ChunkId("shop", "a.txt", "hello"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("default", "should")]
    [InlineData("shop", "must")]
    public async Task Qdrant_searches_filter_by_knowledge_base(string kb, string clause)
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json("""{"result":[]}"""));
        var client = new QdrantClient(new HttpClient(handler),
            Options.Create(new RagOptions { QdrantBaseUrl = "http://qdrant.test:6333", QdrantCollection = "kb" }));

        await client.SearchAsync([1, 0], 5, kb, Ct);

        var (_, body) = Assert.Single(handler.Requests);
        var filter = JsonDocument.Parse(body!).RootElement.GetProperty("filter").GetProperty(clause);
        Assert.Contains(filter.EnumerateArray(), c => c.TryGetProperty("match", out var m) && m.GetProperty("value").GetString() == kb);
        // Points stored before knowledge bases have no "kb" payload: they belong to default only.
        Assert.Equal(kb == "default", filter.EnumerateArray().Any(c => c.TryGetProperty("is_empty", out _)));
    }

    [Fact]
    public void Keyword_search_stays_inside_one_knowledge_base()
    {
        var index = new KeywordIndex();
        index.Upsert(Guid.NewGuid(), "Gold plan costs 900", "a.txt", "default");
        index.Upsert(Guid.NewGuid(), "Gold plan costs 1200", "b.txt", "shop");

        Assert.Equal(["a.txt"], index.Search("gold plan cost", 10).Select(h => h.Source));
        Assert.Equal(["b.txt"], index.Search("gold plan cost", 10, "shop").Select(h => h.Source));
        Assert.Empty(index.Search("gold plan cost", 10, "other"));
    }
}
