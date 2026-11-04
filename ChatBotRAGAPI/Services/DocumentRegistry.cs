using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Durable list of ingested documents, keyed by source (file name or URL), stored under
/// <c>{DataDirectory}/documents/</c>: <c>index.json</c> plus each document's extracted text in <c>{docId}.txt</c>.
/// It knows which vector points belong to which source, so a document can be replaced or deleted
/// without leaving stale chunks, and it is what the worker re-indexes from when the vector store is not persistent.
/// </summary>
public sealed class DocumentRegistry
{
    public sealed record Entry(string Source, string SourceType, DateTimeOffset IngestedAt, IReadOnlyList<Guid> ChunkIds)
    {
        public DocumentInfo ToInfo() => new(Source, SourceType, ChunkIds.Count, IngestedAt);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _indexPath;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;

    public DocumentRegistry(IOptions<RagOptions> options, ILogger<DocumentRegistry> logger, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _directory = Path.Combine(options.Value.DataDirectory, "documents");
        _indexPath = Path.Combine(_directory, "index.json");
        Directory.CreateDirectory(_directory);

        if (!File.Exists(_indexPath)) return;
        try
        {
            foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_indexPath)) ?? [])
                _entries[entry.Source] = entry;
        }
        catch (JsonException ex)
        {
            // Keep the unreadable file for inspection rather than silently overwriting it on the next save.
            var backup = $"{_indexPath}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(_indexPath, backup);
            logger.LogError(ex, "Document index was unreadable; moved to {Backup} and starting empty", backup);
        }
    }

    /// <summary>
    /// Serializes changes to a document's vector points and registry entry (ingestion commit vs. delete),
    /// so a delete can't interleave with a replace of the same document.
    /// </summary>
    public async Task<IDisposable> LockAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        return new Releaser(_gate);
    }

    public IReadOnlyList<Entry> List()
    {
        lock (_sync) return _entries.Values.OrderBy(e => e.Source, StringComparer.Ordinal).ToList();
    }

    public Entry? Get(string source)
    {
        lock (_sync) return _entries.GetValueOrDefault(source);
    }

    public string TextPath(string source) => Path.Combine(_directory, $"{DocId(source)}.txt");

    public Task SaveTextAsync(string source, string text, CancellationToken ct) =>
        File.WriteAllTextAsync(TextPath(source), text, ct);

    /// <param name="ingestedAt">Defaults to now; pass the original time when merely re-indexing unchanged content.</param>
    public void Set(string source, string sourceType, IReadOnlyList<Guid> chunkIds, DateTimeOffset? ingestedAt = null)
    {
        lock (_sync)
        {
            _entries[source] = new Entry(source, sourceType, ingestedAt ?? _time.GetUtcNow(), chunkIds);
            Save();
        }
    }

    public bool Remove(string source)
    {
        lock (_sync)
        {
            if (!_entries.Remove(source)) return false;
            Save();
        }

        File.Delete(TextPath(source));
        return true;
    }

    /// <summary>File-name-safe id for a source (URLs and file names can contain any character).</summary>
    private static Guid DocId(string source) => new(SHA256.HashData(Encoding.UTF8.GetBytes(source)).AsSpan(0, 16));

    private void Save()
    {
        // Write-then-rename so a crash mid-write never leaves a truncated index.
        var temp = _indexPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries.Values, JsonOptions));
        File.Move(temp, _indexPath, overwrite: true);
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
