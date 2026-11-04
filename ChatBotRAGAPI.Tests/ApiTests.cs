using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ChatBotRAGAPI.Tests;

/// <summary>Runs the real app with the in-memory store and local AI fallback, so no Ollama/Qdrant is needed.</summary>
public sealed class RagApiFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("ragapi-").FullName;

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

        Assert.True(File.Exists(Path.Combine(_factory.DataDirectory, $"{jobId}_raw.txt")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_factory.DataDirectory, "inbox")));
    }

    [Fact]
    public async Task Reingesting_identical_content_does_not_duplicate_chunks()
    {
        const string content = "Warranty: all devices carry a two year limited warranty.";
        await WaitForJobAsync(await UploadAsync("a.txt", content));
        await WaitForJobAsync(await UploadAsync("b.txt", content));

        var result = await AskAsync("How long is the warranty?");
        var source = Assert.Single(result.Sources);
        Assert.Equal("b.txt", source.Source); // the second upload overwrote the first point
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
