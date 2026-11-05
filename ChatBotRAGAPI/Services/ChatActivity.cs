namespace ChatBotRAGAPI.Services;

/// <summary>
/// Tracks chat requests in flight so ingestion can step aside for them. On a CPU-only Ollama, an embedding batch
/// and an answer share the same cores, and a question asked during a crawl would otherwise wait behind it.
/// The worker calls <see cref="WaitForIdleAsync"/> between embedding batches; the wait is capped so a steady
/// stream of questions slows ingestion down but never stops it.
/// </summary>
public sealed class ChatActivity
{
    private readonly object _sync = new();
    private int _active;
    private TaskCompletionSource _idle = Completed();

    public bool IsBusy
    {
        get { lock (_sync) return _active > 0; }
    }

    /// <summary>Marks a chat request as running until the returned scope is disposed (idempotent).</summary>
    public IDisposable Begin()
    {
        lock (_sync)
        {
            if (_active++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        return new Scope(this);
    }

    /// <summary>Returns when no chat request is running, or after <paramref name="maxWait"/>, whichever is first.</summary>
    /// <returns>True when it returned because chat went idle (or already was).</returns>
    public async Task<bool> WaitForIdleAsync(TimeSpan maxWait, CancellationToken ct)
    {
        Task idle;
        lock (_sync) idle = _idle.Task;
        if (idle.IsCompleted) return true;

        var finished = await Task.WhenAny(idle, Task.Delay(maxWait, ct));
        ct.ThrowIfCancellationRequested();
        return finished == idle;
    }

    private void End()
    {
        lock (_sync)
        {
            if (--_active == 0) _idle.TrySetResult();
        }
    }

    private static TaskCompletionSource Completed()
    {
        var tcs = new TaskCompletionSource();
        tcs.SetResult();
        return tcs;
    }

    private sealed class Scope(ChatActivity owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.End();
        }
    }
}
