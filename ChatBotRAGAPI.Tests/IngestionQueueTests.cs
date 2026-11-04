using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatBotRAGAPI.Tests;

public class IngestionQueueTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly IngestionQueue _queue;

    public IngestionQueueTests() =>
        _queue = new IngestionQueue(Options.Create(new RagOptions { JobStatusRetentionMinutes = 60 }), _time);

    [Fact]
    public async Task Enqueued_job_is_queued_and_dequeued_with_its_fields()
    {
        var id = _queue.Enqueue("file", "/inbox/abc.txt", "report.txt");

        Assert.Equal(("Queued", (string?)null), _queue.GetStatus(id));
        var job = await _queue.DequeueAsync(TestContext.Current.CancellationToken);
        Assert.Equal(id, job.JobId);
        Assert.Equal("file", job.SourceType);
        Assert.Equal("/inbox/abc.txt", job.SourceValue);
        Assert.Equal("report.txt", job.SourceName);
    }

    [Fact]
    public void Status_transitions_are_recorded()
    {
        var id = _queue.Enqueue("url", "https://example.com", "https://example.com/");
        _queue.MarkRunning(id);
        Assert.Equal("Running", _queue.GetStatus(id).Status);
        _queue.MarkFailed(id, "boom");
        Assert.Equal(("Failed", "boom"), _queue.GetStatus(id));
    }

    [Fact]
    public void Unknown_job_is_not_found() =>
        Assert.Equal("NotFound", _queue.GetStatus(Guid.NewGuid()).Status);

    [Fact]
    public void Finished_jobs_expire_after_retention_but_active_ones_do_not()
    {
        var completed = _queue.Enqueue("url", "a", "a");
        var failed = _queue.Enqueue("url", "b", "b");
        var running = _queue.Enqueue("url", "c", "c");
        var queued = _queue.Enqueue("url", "d", "d");
        _queue.MarkCompleted(completed);
        _queue.MarkFailed(failed, "x");
        _queue.MarkRunning(running);

        _time.Advance(TimeSpan.FromMinutes(59));
        _queue.Enqueue("url", "e", "e"); // pruning happens on enqueue
        Assert.Equal("Completed", _queue.GetStatus(completed).Status);

        _time.Advance(TimeSpan.FromMinutes(2));
        _queue.Enqueue("url", "f", "f");
        Assert.Equal("NotFound", _queue.GetStatus(completed).Status);
        Assert.Equal("NotFound", _queue.GetStatus(failed).Status);
        Assert.Equal("Running", _queue.GetStatus(running).Status);
        Assert.Equal("Queued", _queue.GetStatus(queued).Status);
    }
}
