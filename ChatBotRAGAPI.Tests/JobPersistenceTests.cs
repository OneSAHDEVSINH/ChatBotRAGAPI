using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatBotRAGAPI.Tests;

public sealed class JobPersistenceTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dir = Directory.CreateTempSubdirectory("ragjobs-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 6, 9, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private IOptions<RagOptions> Options_ => Options.Create(new RagOptions { DataDirectory = _dir, JobStatusRetentionMinutes = 60 });
    private IngestionQueue Queue() => new(Options_, _time, new JobStore(Options_));

    [Fact]
    public void Statuses_survive_a_new_queue_on_the_same_data_directory()
    {
        var first = Queue();
        var done = first.Enqueue("file", "inbox/a.txt", "a.txt");
        first.MarkCompleted(done, "Ingested.");

        var second = Queue();

        Assert.Equal(("Completed", "Ingested."), second.GetStatus(done));
    }

    [Fact]
    public void Jobs_a_restart_interrupted_are_reported_as_failed()
    {
        var first = Queue();
        var queued = first.Enqueue("file", "inbox/a.txt", "a.txt");
        var running = first.Enqueue("url", "https://example.com/", "https://example.com/", 10);
        first.MarkRunning(running, "Crawling: 3 page(s) ingested so far.");

        var second = Queue();

        Assert.Equal(("Failed", IngestionQueue.InterruptedMessage), second.GetStatus(queued));
        Assert.Equal(("Failed", IngestionQueue.InterruptedMessage), second.GetStatus(running));
    }

    [Fact]
    public void List_is_newest_first_with_source_and_type()
    {
        var queue = Queue();
        queue.Enqueue("file", "inbox/a.txt", "a.txt");
        _time.Advance(TimeSpan.FromSeconds(5));
        var crawl = queue.Enqueue("url", "https://example.com/", "https://example.com/", 10);

        var jobs = queue.List();

        Assert.Equal(["https://example.com/", "a.txt"], jobs.Select(j => j.Source));
        Assert.Equal(("url", "Queued", crawl), (jobs[0].SourceType, jobs[0].Status, jobs[0].JobId));
    }

    [Fact]
    public void Finished_jobs_expire_from_disk_too()
    {
        var first = Queue();
        var old = first.Enqueue("file", "inbox/a.txt", "a.txt");
        first.MarkCompleted(old);
        _time.Advance(TimeSpan.FromMinutes(61));

        Assert.Equal("NotFound", Queue().GetStatus(old).Status);
    }

    [Fact]
    public void An_unreadable_status_file_is_set_aside()
    {
        File.WriteAllText(Path.Combine(_dir, "jobs.json"), "{ not json");

        Assert.Empty(Queue().List());
        Assert.Single(Directory.GetFiles(_dir, "jobs.json.corrupt-*"));
    }

    [Fact]
    public async Task Through_the_api_jobs_are_listed_and_survive_a_restart()
    {
        Guid jobId;
        using (var app = new RagApiFactory(_dir))
        using (var client = app.CreateClient())
        {
            using var form = new MultipartFormDataContent { { new StringContent("Refund policy: 30 days."), "file", "policy.txt" } };
            jobId = (await (await client.PostAsync("/api/ingestion/upload", form, Ct)).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
            for (var i = 0; i < 200 && (await client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{jobId}", Ct))!.Status != "Completed"; i++)
                await Task.Delay(25, Ct);

            var jobs = await client.GetFromJsonAsync<List<JobInfo>>("/api/ingestion/jobs", Ct);
            Assert.Contains(jobs!, j => j.JobId == jobId && j.Source == "policy.txt" && j.Status == "Completed");
        }

        using var restarted = new RagApiFactory(_dir);
        using var c2 = restarted.CreateClient();
        var status = await c2.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{jobId}", Ct);
        Assert.Equal("Completed", status!.Status);
    }

    [Fact]
    public async Task Uploads_stranded_in_the_inbox_are_cleared_at_startup()
    {
        var inbox = Directory.CreateDirectory(Path.Combine(_dir, "inbox")).FullName;
        var stranded = Path.Combine(inbox, $"{Guid.NewGuid()}.txt");
        File.WriteAllText(stranded, "left behind");

        using var app = new RagApiFactory(_dir);
        using var client = app.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);

        Assert.False(File.Exists(stranded));
    }
}
