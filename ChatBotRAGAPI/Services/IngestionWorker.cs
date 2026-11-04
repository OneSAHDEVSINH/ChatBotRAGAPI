using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ChatBotRAGAPI.Services;

public sealed class IngestionWorker : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly WebScraperService _scraper;
    private readonly FileTextExtractor _extractor;
    private readonly TextChunker _chunker;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IVectorStore _vectorStore;
    private readonly DocumentRegistry _registry;
    private readonly KeywordIndex _keywords;
    private readonly RagOptions _options;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IngestionQueue queue,
        WebScraperService scraper,
        FileTextExtractor extractor,
        TextChunker chunker,
        IEmbeddingClient embeddingClient,
        IVectorStore vectorStore,
        DocumentRegistry registry,
        KeywordIndex keywords,
        IOptions<RagOptions> options,
        ILogger<IngestionWorker> logger)
    {
        _queue = queue;
        _scraper = scraper;
        _extractor = extractor;
        _chunker = chunker;
        _embeddingClient = embeddingClient;
        _vectorStore = vectorStore;
        _registry = registry;
        _keywords = keywords;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        await EnsureVectorStoreAsync(stoppingToken);
        if (_vectorStore.IsPersistent) await RebuildKeywordIndexAsync(stoppingToken);
        else EnqueueReindex();

        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await _queue.DequeueAsync(stoppingToken);
            _queue.MarkRunning(job.JobId);

            try
            {
                await IngestAsync(job, stoppingToken);
                _queue.MarkCompleted(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion failed for JobId {JobId} ({Source})", job.JobId, job.SourceName);
                _queue.MarkFailed(job.JobId, ex.Message);
            }
            finally
            {
                if (job.SourceType == "file") DeleteUpload(job.SourceValue);
            }
        }
    }

    /// <summary>
    /// Ingests one source as a whole document: its previous version (same SourceName) is replaced,
    /// including deleting chunks that no longer exist in the new text.
    /// </summary>
    private async Task IngestAsync(IngestionJob job, CancellationToken ct)
    {
        var text = job.SourceType switch
        {
            "url" => await _scraper.ScrapeTextAsync(job.SourceValue, ct),
            "file" => await _extractor.ExtractAsync(job.SourceValue, ct),
            ReindexSourceType => await File.ReadAllTextAsync(job.SourceValue, ct),
            _ => throw new NotSupportedException($"Unknown source type '{job.SourceType}'.")
        };

        if (job.SourceType != ReindexSourceType)
            await _registry.SaveTextAsync(job.SourceName, text, ct);

        var chunkIds = new List<Guid>();
        var chunks = _chunker.Chunk(text, _options.ChunkSize, _options.ChunkOverlap);
        foreach (var batch in chunks.Chunk(Math.Max(1, _options.EmbeddingBatchSize)))
        {
            var vectors = await _embeddingClient.EmbedBatchAsync(batch, ct);
            var points = batch
                .Select((chunk, i) => new VectorPoint(ChunkId(job.SourceName, chunk), vectors[i], chunk, job.SourceName))
                .ToList();
            await _vectorStore.UpsertAsync(points, ct);
            chunkIds.AddRange(points.Select(p => p.Id));
        }

        using (await _registry.LockAsync(ct))
        {
            var previous = _registry.Get(job.SourceName);
            var stale = previous?.ChunkIds.Except(chunkIds).ToList() ?? [];
            await _vectorStore.DeleteAsync(stale, ct);

            var ids = chunkIds.Distinct().ToList();
            if (job.SourceType == ReindexSourceType)
                _registry.Set(job.SourceName, previous?.SourceType ?? "file", ids, previous?.IngestedAt);
            else
                _registry.Set(job.SourceName, job.SourceType, ids);
        }
    }

    private const string ReindexSourceType = "reindex";

    /// <summary>
    /// A persistent store (Qdrant) keeps its vectors across restarts, but the in-memory keyword index does not.
    /// Rebuild it from the stored texts by re-chunking (no embedding needed). Only chunks whose ids the registry
    /// knows are added, so a changed ChunkSize can't put chunks in the keyword index that the store doesn't have.
    /// </summary>
    private async Task RebuildKeywordIndexAsync(CancellationToken ct)
    {
        var added = 0;
        foreach (var doc in _registry.List())
        {
            var path = _registry.TextPath(doc.Source);
            if (!File.Exists(path)) continue;

            var known = doc.ChunkIds.ToHashSet();
            var text = await File.ReadAllTextAsync(path, ct);
            foreach (var chunk in _chunker.Chunk(text, _options.ChunkSize, _options.ChunkOverlap))
            {
                var id = ChunkId(doc.Source, chunk);
                if (!known.Contains(id)) continue;
                _keywords.Upsert(id, chunk, doc.Source);
                added++;
            }
        }

        if (added > 0) _logger.LogInformation("Keyword index rebuilt with {Count} chunk(s)", added);
    }

    /// <summary>
    /// The in-memory store starts empty on every run; rebuild it from the stored document texts.
    /// These go through the normal queue, so they are visible via job status and retried per document.
    /// </summary>
    private void EnqueueReindex()
    {
        var documents = _registry.List();
        foreach (var doc in documents)
        {
            var path = _registry.TextPath(doc.Source);
            if (File.Exists(path))
                _queue.Enqueue(ReindexSourceType, path, doc.Source);
            else
                _logger.LogWarning("Stored text for {Source} is missing; it cannot be re-indexed", doc.Source);
        }

        if (documents.Count > 0)
            _logger.LogInformation("Re-indexing {Count} stored document(s)", documents.Count);
    }

    /// <summary>
    /// An exception escaping ExecuteAsync stops the whole host, so a vector store that is
    /// unreachable at startup (e.g. Qdrant still booting) is retried with backoff instead.
    /// Jobs stay queued meanwhile.
    /// </summary>
    private async Task EnsureVectorStoreAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                await _vectorStore.EnsureCollectionAsync(stoppingToken);
                return;
            }
            catch (InvalidOperationException)
            {
                throw; // configuration error (e.g. vector size mismatch): retrying won't help
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Vector store not ready; retrying in {Delay}", delay);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
    }

    /// <summary>
    /// Uploaded files are only a hand-off from the controller; the extracted text is kept in
    /// {jobId}_raw.txt, so the original is removed whether ingestion succeeded or failed.
    /// </summary>
    private void DeleteUpload(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete uploaded file {Path}", path);
        }
    }

    /// <summary>
    /// Deterministic id per (source, chunk text): re-ingesting a source overwrites its unchanged chunks
    /// instead of duplicating them. The source is part of the id so two documents containing the same
    /// text own separate points, and deleting one can't remove the other's.
    /// </summary>
    private static Guid ChunkId(string source, string chunk)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}\0{chunk}"));
        return new Guid(hash.AsSpan(0, 16));
    }
}
