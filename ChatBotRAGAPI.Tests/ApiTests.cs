using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChatBotRAGAPI.Tests;

/// <summary>Runs the real app with the in-memory store and local AI fallback, so no Ollama/Qdrant is needed.</summary>
public sealed class RagApiFactory(string? dataDirectory = null) : WebApplicationFactory<Program>
{
    private readonly bool _ownsDirectory = dataDirectory is null;
    public string DataDirectory { get; } = dataDirectory ?? Directory.CreateTempSubdirectory("ragapi-").FullName;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Rag:UseInMemoryVectorStore", "true");
        builder.UseSetting("Rag:UseLocalAiFallback", "true");
        builder.UseSetting("Rag:DataDirectory", DataDirectory);
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
        builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Error"); // test server: no https port / body-size features
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (_ownsDirectory)
            try { Directory.Delete(DataDirectory, recursive: true); } catch (IOException) { }
    }
}

public sealed class ApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly RagApiFactory _factory = new();
    private readonly HttpClient _client;

    public ApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<Guid> UploadAsync(string fileName, string content)
    {
        using var form = new MultipartFormDataContent { { new StringContent(content), "file", fileName } };
        var response = await _client.PostAsync("/api/ingestion/upload", form, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
    }

    private async Task<JobStatusResponse> WaitForJobAsync(Guid jobId)
    {
        for (var i = 0; i < 100; i++)
        {
            var status = await _client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{jobId}", Ct);
            if (status!.Status is "Completed" or "Failed") return status;
            await Task.Delay(50, Ct);
        }
        throw new TimeoutException($"Job {jobId} did not finish.");
    }

    private async Task<List<DocumentInfo>> ListDocumentsAsync() =>
        await _client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct) ?? [];

    private async Task DeleteDocumentAsync(string source, HttpStatusCode expected) =>
        Assert.Equal(expected,
            (await _client.DeleteAsync($"/api/documents?source={Uri.EscapeDataString(source)}", Ct)).StatusCode);

    private async Task<AskResponse> AskAsync(string question)
    {
        var response = await _client.PostAsJsonAsync("/api/chat/ask", new AskRequest(question), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AskResponse>(Ct))!;
    }

    [Fact]
    public async Task Health_endpoint_is_up() =>
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync("/health", Ct)).StatusCode);

    [Fact]
    public async Task Ask_without_question_is_bad_request() =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/chat/ask", new AskRequest(" "), Ct)).StatusCode);

    [Fact]
    public async Task Ask_with_empty_store_has_no_data()
    {
        var result = await AskAsync("What is the refund policy?");
        Assert.Equal(RagService.NoDataAnswer, result.Answer);
        Assert.Empty(result.Sources);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.5:8080/")]
    public async Task Scrape_rejects_unsafe_urls(string url) =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsJsonAsync("/api/ingestion/scrape", new ScrapeRequest(url), Ct)).StatusCode);

    [Fact]
    public async Task Upload_rejects_unsupported_types()
    {
        using var form = new MultipartFormDataContent { { new StringContent("x"), "file", "evil.exe" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/ingestion/upload", form, Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_job_is_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/ingestion/{Guid.NewGuid()}", Ct)).StatusCode);

    [Fact]
    public async Task Uploaded_file_is_ingested_cited_and_cleaned_up()
    {
        var jobId = await UploadAsync("handbook.txt",
            "Refund policy: customers may request a full refund within 30 days of purchase.\n\n" +
            "Office hours: the support desk is open Monday to Friday, 9am to 5pm.");

        Assert.Equal("Completed", (await WaitForJobAsync(jobId)).Status);

        var result = await AskAsync("What is the refund policy?");
        Assert.Contains("30 days", result.Answer);
        Assert.Contains("[1]", result.Answer);
        Assert.Equal("handbook.txt", result.Sources[0].Source);
        Assert.Equal(1, result.Sources[0].Id);

        Assert.Empty(Directory.GetFiles(Path.Combine(_factory.DataDirectory, "inbox")));
    }

    [Fact]
    public async Task Reingesting_a_source_replaces_it_and_removes_stale_chunks()
    {
        await WaitForJobAsync(await UploadAsync("policy.txt", "Warranty: all devices carry a two year limited warranty."));
        await WaitForJobAsync(await UploadAsync("policy.txt", "Warranty: all devices carry a five year extended warranty."));

        var result = await AskAsync("How long is the warranty?");
        Assert.Contains("five year", result.Answer);
        Assert.DoesNotContain(result.Context, c => c.Contains("two year"));

        var doc = Assert.Single(await ListDocumentsAsync());
        Assert.Equal(("policy.txt", "file", 1), (doc.Source, doc.SourceType, doc.ChunkCount));
    }

    [Fact]
    public async Task Identical_content_under_different_sources_is_kept_per_document()
    {
        const string content = "Warranty: all devices carry a two year limited warranty.";
        await WaitForJobAsync(await UploadAsync("a.txt", content));
        await WaitForJobAsync(await UploadAsync("b.txt", content));
        Assert.Equal(["a.txt", "b.txt"], (await ListDocumentsAsync()).Select(d => d.Source));

        // Deleting one copy must not remove the other's chunk.
        await DeleteDocumentAsync("a.txt", HttpStatusCode.NoContent);
        var result = await AskAsync("How long is the warranty?");
        Assert.Equal("b.txt", Assert.Single(result.Sources).Source);
    }

    [Fact]
    public async Task Deleting_a_document_removes_it_from_search_and_disk()
    {
        await WaitForJobAsync(await UploadAsync("handbook.txt", "Refund policy: a full refund within 30 days of purchase."));
        var documentsDir = Path.Combine(_factory.DataDirectory, "documents");
        Assert.Single(Directory.GetFiles(documentsDir, "*.txt"));

        await DeleteDocumentAsync("handbook.txt", HttpStatusCode.NoContent);

        Assert.Equal(RagService.NoDataAnswer, (await AskAsync("What is the refund policy?")).Answer);
        Assert.Empty(await ListDocumentsAsync());
        Assert.Empty(Directory.GetFiles(documentsDir, "*.txt"));
        await DeleteDocumentAsync("handbook.txt", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_requires_a_source() =>
        await DeleteDocumentAsync("", HttpStatusCode.BadRequest);

    [Fact]
    public async Task Documents_are_reindexed_after_a_restart()
    {
        await WaitForJobAsync(await UploadAsync("handbook.txt", "Refund policy: a full refund within 30 days of purchase."));
        var ingestedAt = Assert.Single(await ListDocumentsAsync()).IngestedAt;

        // Same data directory, fresh process state: the in-memory store starts empty and is rebuilt from disk.
        using var restarted = new RagApiFactory(_factory.DataDirectory);
        using var client = restarted.CreateClient();

        AskResponse? result = null;
        for (var i = 0; i < 100 && (result is null || result.Sources.Count == 0); i++)
        {
            if (i > 0) await Task.Delay(50, Ct);
            var response = await client.PostAsJsonAsync("/api/chat/ask", new AskRequest("What is the refund policy?"), Ct);
            result = await response.Content.ReadFromJsonAsync<AskResponse>(Ct);
        }

        Assert.Contains("30 days", result!.Answer);
        Assert.Equal("handbook.txt", result.Sources[0].Source);
        var doc = Assert.Single(await client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct) ?? []);
        Assert.Equal(("file", ingestedAt), (doc.SourceType, doc.IngestedAt)); // re-indexing keeps the original metadata
    }

    [Fact]
    public async Task Unrelated_question_is_refused_after_ingestion()
    {
        await WaitForJobAsync(await UploadAsync("menu.txt", "The cafeteria serves pasta on Tuesdays and curry on Thursdays."));
        // Shares only stopwords ("the", "on") with the document; those must not count as a match.
        var result = await AskAsync("Who won the football championship on the weekend?");
        Assert.Equal(RagService.NoDataAnswer, result.Answer);
    }
}
