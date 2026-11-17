using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Durable list of ingested documents, keyed by knowledge base and source (file name or URL), stored under
/// <c>{DataDirectory}/documents/</c>: <c>index.json</c> plus each document's extracted text in <c>{docId}.txt</c>
/// and its chunk embeddings in <c>{docId}.vec</c>.
/// It knows which vector points belong to which source, so a document can be replaced or deleted
/// without leaving stale chunks, and it is what the worker re-indexes from when the vector store is not persistent.
/// </summary>
public sealed class DocumentRegistry
{
    /// <param name="KnowledgeBase">Entries written before knowledge bases have none and read as "default".</param>
    public sealed record Entry(string Source, string SourceType, DateTimeOffset IngestedAt, IReadOnlyList<Guid> ChunkIds,
        string KnowledgeBase = KnowledgeBaseStore.Default)
    {
        public DocumentInfo ToInfo() => new(Source, SourceType, ChunkIds.Count, IngestedAt, KnowledgeBase);
    }

    private static string Key(string knowledgeBase, string source) => $"{knowledgeBase}\0{source}";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _directory;
    private readonly string _indexPath;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _sync = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeProvider _time;
    private readonly ILogger<DocumentRegistry> _logger;

    public DocumentRegistry(IOptions<RagOptions> options, ILogger<DocumentRegistry> logger, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _logger = logger;
        _directory = Path.Combine(options.Value.DataDirectory, "documents");
        _indexPath = Path.Combine(_directory, "index.json");
        Directory.CreateDirectory(_directory);
        Reload();
    }

    /// <summary>Reads index.json again (after a restore replaced it). Callers that change documents hold <see cref="LockAsync"/>.</summary>
    public void Reload()
    {
        lock (_sync)
        {
            _entries.Clear();
            if (!File.Exists(_indexPath)) return;
            try
            {
                foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_indexPath)) ?? [])
                {
                    var e = entry.KnowledgeBase is null ? entry with { KnowledgeBase = KnowledgeBaseStore.Default } : entry;
                    _entries[Key(e.KnowledgeBase, e.Source)] = e;
                }
            }
            catch (JsonException ex)
            {
                // Keep the unreadable file for inspection rather than silently overwriting it on the next save.
                var backup = $"{_indexPath}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmss}";
                File.Move(_indexPath, backup);
                _logger.LogError(ex, "Document index was unreadable; moved to {Backup} and starting empty", backup);
            }
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

    /// <param name="knowledgeBase">Only this knowledge base's documents; null = all of them.</param>
    public IReadOnlyList<Entry> List(string? knowledgeBase = null)
    {
        lock (_sync)
            return _entries.Values.Where(e => knowledgeBase is null || e.KnowledgeBase == knowledgeBase)
                .OrderBy(e => e.KnowledgeBase, StringComparer.Ordinal).ThenBy(e => e.Source, StringComparer.Ordinal).ToList();
    }

    public int Count(string knowledgeBase)
    {
        lock (_sync) return _entries.Values.Count(e => e.KnowledgeBase == knowledgeBase);
    }

    public Entry? Get(string knowledgeBase, string source)
    {
        lock (_sync) return _entries.GetValueOrDefault(Key(knowledgeBase, source));
    }

    public string TextPath(string knowledgeBase, string source) => Path.Combine(_directory, $"{DocId(knowledgeBase, source)}.txt");

    /// <summary>The document's cached chunk embeddings (<see cref="VectorFile"/>).</summary>
    public string VectorsPath(string knowledgeBase, string source) => Path.Combine(_directory, $"{DocId(knowledgeBase, source)}.vec");

    public Task SaveTextAsync(string knowledgeBase, string source, string text, CancellationToken ct) =>
        File.WriteAllTextAsync(TextPath(knowledgeBase, source), text, ct);

    /// <param name="ingestedAt">Defaults to now; pass the original time when merely re-indexing unchanged content.</param>
    public void Set(string knowledgeBase, string source, string sourceType, IReadOnlyList<Guid> chunkIds, DateTimeOffset? ingestedAt = null)
    {
        lock (_sync)
        {
            _entries[Key(knowledgeBase, source)] = new Entry(source, sourceType, ingestedAt ?? _time.GetUtcNow(), chunkIds, knowledgeBase);
            Save();
        }
    }

    public bool Remove(string knowledgeBase, string source)
    {
        lock (_sync)
        {
            if (!_entries.Remove(Key(knowledgeBase, source))) return false;
            Save();
        }

        File.Delete(TextPath(knowledgeBase, source));
        File.Delete(VectorsPath(knowledgeBase, source));
        return true;
    }

    /// <summary>
    /// File-name-safe id for a document (URLs and file names can contain any character). "default" keeps the id it had
    /// before knowledge bases existed (the source alone), so existing data needs no migration.
    /// </summary>
    private static Guid DocId(string knowledgeBase, string source) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        knowledgeBase == KnowledgeBaseStore.Default ? source : $"{knowledgeBase}\0{source}")).AsSpan(0, 16));

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
