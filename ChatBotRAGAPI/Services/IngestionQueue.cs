using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ChatBotRAGAPI.Services;

public sealed class IngestionQueue
{
    public const string InterruptedMessage = "Interrupted by a restart before it finished; ingest it again.";

    private readonly Channel<IngestionJob> _channel = Channel.CreateUnbounded<IngestionJob>();
    private readonly ConcurrentDictionary<Guid, JobInfo> _status = new();
    private readonly TimeSpan _retention;
    private readonly TimeProvider _time;
    private readonly JobStore? _store;
    private readonly object _saveLock = new();

    /// <param name="store">Persists statuses across restarts; null keeps them in memory only.</param>
    public IngestionQueue(IOptions<RagOptions> options, TimeProvider? time = null, JobStore? store = null)
    {
        _retention = TimeSpan.FromMinutes(Math.Max(1, options.Value.JobStatusRetentionMinutes));
        _time = time ?? TimeProvider.System;
        _store = store;

        if (_store is null) return;

        // A job that was queued or running when the process stopped will never finish: the queue itself is not
        // persisted, and an interrupted upload's file is cleared from the inbox at startup.
        foreach (var job in _store.Load())
        {
            _status[job.JobId] = job.Status is "Queued" or "Running"
                ? job with { Status = "Failed", Message = InterruptedMessage, UpdatedAt = _time.GetUtcNow() }
                : job;
        }
        PruneExpired();
        Save();
    }

    public Guid Enqueue(string sourceType, string sourceValue, string sourceName, int maxPages = 1, bool removeMissing = false)
    {
        PruneExpired();

        var job = new IngestionJob(Guid.NewGuid(), sourceType, sourceValue, sourceName, maxPages, removeMissing);
        var now = _time.GetUtcNow();
        _status[job.JobId] = new JobInfo(job.JobId, sourceType, sourceName, "Queued", null, now, now);
        Save();
        _channel.Writer.TryWrite(job);
        return job.JobId;
    }

    public ValueTask<IngestionJob> DequeueAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);

    public void MarkRunning(Guid jobId, string? progress = null) => Set(jobId, "Running", progress);
    public void MarkCompleted(Guid jobId, string? summary = null) => Set(jobId, "Completed", summary);
    public void MarkFailed(Guid jobId, string message) => Set(jobId, "Failed", message);

    public (string Status, string? Message) GetStatus(Guid jobId) =>
        _status.TryGetValue(jobId, out var s) ? (s.Status, s.Message) : ("NotFound", null);

    /// <summary>Recent jobs, newest first (finished ones are kept for JobStatusRetentionMinutes).</summary>
    public IReadOnlyList<JobInfo> List(int limit = 100)
    {
        PruneExpired();
        return _status.Values.OrderByDescending(j => j.CreatedAt).Take(Math.Max(1, limit)).ToList();
    }

    private void Set(Guid jobId, string status, string? message = null)
    {
        if (!_status.TryGetValue(jobId, out var job)) return;
        _status[jobId] = job with { Status = status, Message = message, UpdatedAt = _time.GetUtcNow() };
        Save();
    }

    /// <summary>Drops finished jobs older than the retention window so the status map can't grow forever.</summary>
    private void PruneExpired()
    {
        var cutoff = _time.GetUtcNow() - _retention;
        foreach (var (id, state) in _status)
        {
            if (state.Status is "Completed" or "Failed" && state.UpdatedAt < cutoff)
                _status.TryRemove(id, out _);
        }
    }

    private void Save()
    {
        if (_store is null) return;
        lock (_saveLock) _store.Save(_status.Values.ToList());
    }
}
