using ChatBotRAGAPI.Contracts;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChatBotRAGAPI.Services;

public sealed class IngestionQueue
{
    private readonly Channel<IngestionJob> _channel = Channel.CreateUnbounded<IngestionJob>();
    private readonly ConcurrentDictionary<Guid, (string Status, string? Message)> _status = new();

    public Guid Enqueue(string sourceType, string sourceValue)
    {
        var job = new IngestionJob(Guid.NewGuid(), sourceType, sourceValue);
        _status[job.JobId] = ("Queued", null);
        _channel.Writer.TryWrite(job);
        return job.JobId;
    }

    public ValueTask<IngestionJob> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void MarkRunning(Guid jobId) => _status[jobId] = ("Running", null);
    public void MarkCompleted(Guid jobId) => _status[jobId] = ("Completed", null);
    public void MarkFailed(Guid jobId, string message) => _status[jobId] = ("Failed", message);

    public (string Status, string? Message) GetStatus(Guid jobId) =>
        _status.TryGetValue(jobId, out var s) ? s : ("NotFound", null);
}