using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatBotRAGAPI.Tests;

public sealed class CrawlSchedulerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ragsched-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 2, 0, 0, TimeSpan.Zero));
    private readonly IngestionQueue _queue;

    public CrawlSchedulerTests() => _queue = new IngestionQueue(Options(), _time);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private IOptions<RagOptions> Options() => Microsoft.Extensions.Options.Options.Create(new RagOptions { DataDirectory = _dir, MaxCrawlPages = 50 });
    private CrawlScheduler Scheduler() => new(_queue, Options(), _time);

    [Fact]
    public void A_new_schedule_runs_at_once_then_every_interval()
    {
        var scheduler = Scheduler();
        var (schedule, error) = scheduler.Add(new CrawlScheduleRequest("https://example.com/", 20, true, 24));
        Assert.Null(error);

        Assert.Equal(1, scheduler.RunDue());               // due immediately
        var first = scheduler.List()[0];
        Assert.Equal("Queued", first.LastJobStatus);
        Assert.Equal(_time.GetUtcNow().AddHours(24), first.NextRunAt);

        _queue.MarkCompleted(first.LastJobId!.Value, "Ingested 18 page(s).");
        Assert.Equal(0, scheduler.RunDue());               // not due again yet
        _time.Advance(TimeSpan.FromHours(24));
        Assert.Equal(1, scheduler.RunDue());
        Assert.NotEqual(first.LastJobId, scheduler.List()[0].LastJobId);
    }

    [Fact]
    public void A_run_is_skipped_while_the_previous_one_is_still_going()
    {
        var scheduler = Scheduler();
        scheduler.Add(new CrawlScheduleRequest("https://example.com/", 20, true, 1));
        scheduler.RunDue();
        var job = scheduler.List()[0].LastJobId!.Value;
        _queue.MarkRunning(job, "Crawling: 5 page(s) ingested so far.");

        _time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(0, scheduler.RunDue());
        Assert.Equal(Guid.Empty, scheduler.RunNow(scheduler.List()[0].Id));

        _queue.MarkCompleted(job);
        Assert.Equal(1, scheduler.RunDue());               // catches up once the crawl finishes
    }

    [Fact]
    public void Run_now_starts_a_crawl_and_pushes_the_next_run_back()
    {
        var scheduler = Scheduler();
        var id = scheduler.Add(new CrawlScheduleRequest("https://example.com/", 5, false, 6)).Schedule!.Id;

        var job = scheduler.RunNow(id);

        Assert.NotNull(job);
        Assert.NotEqual(Guid.Empty, job);
        Assert.Equal(_time.GetUtcNow().AddHours(6), scheduler.List()[0].NextRunAt);
        Assert.Null(scheduler.RunNow(Guid.NewGuid()));
    }

    [Fact]
    public void Schedules_survive_a_restart()
    {
        var id = Scheduler().Add(new CrawlScheduleRequest("https://example.com/docs/", 10, true, 12)).Schedule!.Id;

        var reloaded = Assert.Single(Scheduler().List());

        Assert.Equal((id, "https://example.com/docs/", 10, true, 12), (reloaded.Id, reloaded.Url, reloaded.MaxPages, reloaded.RemoveMissing, reloaded.IntervalHours));
    }

    [Theory]
    [InlineData("ftp://example.com/", 20, true, 24, "Invalid URL")]
    [InlineData("http://192.168.1.10/", 20, true, 24, "private")]
    [InlineData("https://example.com/", 51, true, 24, "maxPages")]
    [InlineData("https://example.com/", 1, true, 24, "removeMissing needs a crawl")]
    [InlineData("https://example.com/", 20, true, 0, "intervalHours")]
    [InlineData("https://example.com/", 20, true, 721, "intervalHours")]
    public void Bad_schedules_are_refused(string url, int pages, bool remove, int hours, string error)
    {
        var (schedule, message) = Scheduler().Add(new CrawlScheduleRequest(url, pages, remove, hours));
        Assert.Null(schedule);
        Assert.Contains(error, message);
    }

    [Fact]
    public void The_number_of_schedules_is_capped()
    {
        var scheduler = Scheduler();
        for (var i = 0; i < CrawlScheduler.MaxSchedules; i++)
            Assert.Null(scheduler.Add(new CrawlScheduleRequest($"https://example{i}.com/")).Error);

        Assert.Contains("At most", scheduler.Add(new CrawlScheduleRequest("https://one-more.com/")).Error);
    }
}

public sealed class ScheduleApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeSite _site = new FakeSite()
        .Page("/", "Acme", "Acme builds rockets.", "/about")
        .Page("/about", "About Acme", "Acme was founded in 1949.");

    private readonly RagApiFactory _factory;
    private readonly HttpClient _client;

    public ScheduleApiTests()
    {
        _factory = new RagApiFactory(
            settings: new Dictionary<string, string> { ["Rag:CrawlDelayMilliseconds"] = "0" },
            services: s => s.AddHttpClient<WebScraperService>().ConfigurePrimaryHttpMessageHandler(() => _site.Handler));
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task Create_run_follow_and_delete_a_schedule()
    {
        var created = await _client.PostAsJsonAsync("/api/schedules", new CrawlScheduleRequest("https://site.test/", 5, true, 24), Ct);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var schedule = (await created.Content.ReadFromJsonAsync<CrawlScheduleInfo>(Ct))!;

        // The background service may already have started the first run; either way, a run completes.
        await _client.PostAsync($"/api/schedules/{schedule.Id}/run", null, Ct);
        CrawlScheduleInfo? listed = null;
        for (var i = 0; i < 200; i++)
        {
            listed = (await _client.GetFromJsonAsync<List<CrawlScheduleInfo>>("/api/schedules", Ct))!.Single();
            if (listed.LastJobStatus == "Completed") break;
            await Task.Delay(25, Ct);
        }
        Assert.Equal("Completed", listed!.LastJobStatus);
        Assert.StartsWith("Ingested 2 page(s).", listed.LastJobMessage);

        var docs = await _client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct);
        Assert.Contains(docs!, d => d.Source == "https://site.test/about");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/schedules/{schedule.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/schedules/{schedule.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/schedules/{schedule.Id}/run", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task Invalid_schedules_are_rejected()
    {
        var response = await _client.PostAsJsonAsync("/api/schedules", new CrawlScheduleRequest("http://10.0.0.1/", 5, true, 24), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Schedules_require_the_api_key()
    {
        using var secured = new RagApiFactory(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "k" });
        using var client = secured.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/schedules", Ct)).StatusCode);
    }
}
