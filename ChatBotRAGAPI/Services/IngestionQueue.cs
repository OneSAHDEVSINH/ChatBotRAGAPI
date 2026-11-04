using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChatBotRAGAPI.Services;

public sealed class IngestionQueue
{
    private sealed record JobState(string Status, string? Message, DateTimeOffset UpdatedAt)
    {
        public bool IsFinished => Status is "Completed" or "Failed";
    }

    private readonly Channel<IngestionJob> _channel = Channel.CreateUnbounded<IngestionJob>();
    private readonly ConcurrentDictionary<Guid, JobState> _status = new();
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;

    public IngestionQueue(IOptions<RagOptions> options, TimeProvider? time = null)
    {
        _retention = TimeSpan.FromMinutes(Math.Max(1, options.Value.JobStatusRetentionMinutes));
        _time = time ?? TimeProvider.System;
    }

    public Guid Enqueue(string sourceType, string sourceValue, string sourceName)
    {
        PruneExpired();

        var job = new IngestionJob(Guid.NewGuid(), sourceType, sourceValue, sourceName);
        Set(job.JobId, "Queued");
        _channel.Writer.TryWrite(job);
        return job.JobId;
    }

    public ValueTask<IngestionJob> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void MarkRunning(Guid jobId) => Set(jobId, "Running");
    public void MarkCompleted(Guid jobId) => Set(jobId, "Completed");
    public void MarkFailed(Guid jobId, string message) => Set(jobId, "Failed", message);

    public (string Status, string? Message) GetStatus(Guid jobId) =>
        _status.TryGetValue(jobId, out var s) ? (s.Status, s.Message) : ("NotFound", null);

    private void Set(Guid jobId, string status, string? message = null) =>
        _status[jobId] = new JobState(status, message, _time.GetUtcNow());

    /// <summary>Drops finished jobs older than the retention window so the status map can't grow forever.</summary>
    private void PruneExpired()
    {
        var cutoff = _time.GetUtcNow() - _retention;
        foreach (var (id, state) in _status)
        {
            if (state.IsFinished && state.UpdatedAt < cutoff)
                _status.TryRemove(id, out _);
        }
    }
}
