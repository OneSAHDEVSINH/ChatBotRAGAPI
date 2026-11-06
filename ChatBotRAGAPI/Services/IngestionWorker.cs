using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ChatBotRAGAPI.Services;

public sealed class IngestionWorker : BackgroundService
{
    private readonly IngestionQueue _queue;
    private readonly WebCrawler _crawler;
    private readonly FileTextExtractor _extractor;
    private readonly TextChunker _chunker;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly IVectorStore _vectorStore;
    private readonly DocumentRegistry _registry;
    private readonly KeywordIndex _keywords;
    private readonly ChatActivity _chat;
    private readonly Health.WorkerState _state;
    private readonly RagOptions _options;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IngestionQueue queue,
        WebCrawler crawler,
        FileTextExtractor extractor,
        TextChunker chunker,
        IEmbeddingClient embeddingClient,
        IVectorStore vectorStore,
        DocumentRegistry registry,
        KeywordIndex keywords,
        ChatActivity chat,
        Health.WorkerState state,
        IOptions<RagOptions> options,
        ILogger<IngestionWorker> logger)
    {
        _queue = queue;
        _crawler = crawler;
        _extractor = extractor;
        _chunker = chunker;
        _embeddingClient = embeddingClient;
        _vectorStore = vectorStore;
        _registry = registry;
        _keywords = keywords;
        _chat = chat;
        _state = state;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        WarnAboutLegacyFiles();
        ClearInbox();
        await EnsureVectorStoreAsync(stoppingToken);
        _state.StoreReady = true;
        if (_vectorStore.IsPersistent) await RebuildKeywordIndexAsync(stoppingToken);
        else EnqueueReindex();

        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await _queue.DequeueAsync(stoppingToken);
            _queue.MarkRunning(job.JobId);

            try
            {
                var summary = await IngestAsync(job, stoppingToken);
                _queue.MarkCompleted(job.JobId, summary);
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

    /// <summary>Runs one job; returns a summary for the job status (crawls only), or null.</summary>
    private async Task<string?> IngestAsync(IngestionJob job, CancellationToken ct)
    {
        switch (job.SourceType)
        {
            case "url":
                var pages = 0;
                var result = await _crawler.CrawlAsync(new Uri(job.SourceValue), job.SourceName, job.MaxPages,
                    async (source, page, token) =>
                    {
                        await StoreDocumentAsync(source, "url", page.Text, saveText: true, token);
                        if (job.MaxPages > 1) _queue.MarkRunning(job.JobId, $"Crawling: {++pages} page(s) ingested so far.");
                    }, ct);
                if (job.MaxPages <= 1) return null;
                return job.RemoveMissing ? $"{result} {await RemoveMissingPagesAsync(result, ct)}" : result.ToString();

            case "file":
                await StoreDocumentAsync(job.SourceName, "file", await _extractor.ExtractAsync(job.SourceValue, ct), saveText: true, ct);
                return null;

            case ReindexSourceType:
                await StoreDocumentAsync(job.SourceName, ReindexSourceType, await File.ReadAllTextAsync(job.SourceValue, ct), saveText: false, ct);
                return null;

            default:
                throw new NotSupportedException($"Unknown source type '{job.SourceType}'.");
        }
    }

    /// <summary>
    /// After a complete crawl, deletes the site's page documents the crawl no longer found. Never after a crawl cut
    /// short by maxPages (pages it did not reach may still exist), and never a page that failed with a possibly
    /// temporary error (<see cref="WebCrawler.CrawlResult.Kept"/>).
    /// </summary>
    private async Task<string> RemoveMissingPagesAsync(WebCrawler.CrawlResult result, CancellationToken ct)
    {
        if (!result.Complete || result.Kept is null || result.InScope is null)
            return "Nothing removed: the crawl stopped at maxPages, so pages it did not reach may still exist.";

        var removed = new List<string>();
        using (await _registry.LockAsync(ct))
        {
            foreach (var entry in _registry.List())
            {
                if (entry.SourceType != "url" || !Uri.TryCreate(entry.Source, UriKind.Absolute, out var url)) continue;
                if (!result.InScope(url) || result.Kept.Contains(WebCrawler.Key(url))) continue;

                await _vectorStore.DeleteAsync(entry.ChunkIds.ToList(), ct);
                _registry.Remove(entry.Source);
                removed.Add(entry.Source);
            }
        }

        foreach (var source in removed) _logger.LogInformation("Crawl: removed {Source}, no longer on the site", source);
        return removed.Count == 0 ? "No pages had disappeared." : $"Removed {removed.Count} page(s) no longer on the site.";
    }

    /// <summary>
    /// Stores one source as a whole document: its previous version (same source name) is replaced,
    /// including deleting chunks that no longer exist in the new text.
    /// </summary>
    private async Task StoreDocumentAsync(string source, string sourceType, string text, bool saveText, CancellationToken ct)
    {
        if (saveText)
            await _registry.SaveTextAsync(source, text, ct);

        // Chunks whose text is unchanged keep their id, so their vectors come from the cache instead of the model.
        var modelId = _embeddingClient.ModelId;
        var cached = VectorFile.Read(_registry.VectorsPath(source), modelId, _logger);
        var vectorsById = new Dictionary<Guid, float[]>();
        var embedded = 0;

        var chunkIds = new List<Guid>();
        var chunks = _chunker.Chunk(text, _options.ChunkSize, _options.ChunkOverlap);
        foreach (var batch in chunks.Chunk(Math.Max(1, _options.EmbeddingBatchSize)))
        {
            var ids = batch.Select(chunk => ChunkId(source, chunk)).ToList();
            var missing = Enumerable.Range(0, batch.Length).Where(i => !cached.ContainsKey(ids[i])).ToList();
            if (missing.Count > 0)
            {
                if (_options.PrioritizeChat)
                    await _chat.WaitForIdleAsync(TimeSpan.FromSeconds(Math.Max(0, _options.ChatPriorityMaxWaitSeconds)), ct);
                var fresh = await _embeddingClient.EmbedBatchAsync(missing.Select(i => batch[i]).ToList(), ct);
                for (var k = 0; k < missing.Count; k++) cached[ids[missing[k]]] = fresh[k];
                embedded += missing.Count;
            }

            var points = batch.Select((chunk, i) => new VectorPoint(ids[i], cached[ids[i]], chunk, source)).ToList();
            await _vectorStore.UpsertAsync(points, ct);
            chunkIds.AddRange(ids);
            foreach (var p in points) vectorsById[p.Id] = p.Vector;
        }

        if (chunks.Count > 0)
            _logger.LogInformation("{Source}: {Chunks} chunk(s), {Embedded} embedded, {Reused} from cache",
                source, chunks.Count, embedded, chunks.Count - embedded);

        using (await _registry.LockAsync(ct))
        {
            // Only the current chunks are kept, so the cache never grows with old versions of the text.
            VectorFile.Write(_registry.VectorsPath(source), modelId, vectorsById);

            var previous = _registry.Get(source);
            var stale = previous?.ChunkIds.Except(chunkIds).ToList() ?? [];
            await _vectorStore.DeleteAsync(stale, ct);

            var ids = chunkIds.Distinct().ToList();
            if (sourceType == ReindexSourceType)
                _registry.Set(source, previous?.SourceType ?? "file", ids, previous?.IngestedAt);
            else
                _registry.Set(source, sourceType, ids);
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
    /// Uploads wait in the inbox only until their job runs. At startup the queue is empty, so anything still there
    /// belonged to a job a restart interrupted (now reported as Failed) and will never be processed.
    /// </summary>
    private void ClearInbox()
    {
        var inbox = Path.Combine(_options.DataDirectory, "inbox");
        if (!Directory.Exists(inbox)) return;
        var stranded = Directory.GetFiles(inbox);
        foreach (var file in stranded) DeleteUpload(file);
        if (stranded.Length > 0)
            _logger.LogWarning("Removed {Count} upload(s) left in the inbox by jobs a restart interrupted", stranded.Length);
    }

    /// <summary>
    /// Before the document registry existed, extracted text was kept as <c>{jobId}_raw.txt</c> with no record of
    /// its source, so it can't be imported as a named, citable document. Say so instead of ignoring it silently.
    /// </summary>
    private void WarnAboutLegacyFiles()
    {
        var legacy = Directory.GetFiles(_options.DataDirectory, "*_raw.txt");
        if (legacy.Length > 0)
            _logger.LogWarning(
                "{Count} file(s) named *_raw.txt in {Directory} predate the document registry and are not searchable. " +
                "Re-ingest their sources (files or URLs) to make them answerable, then delete the *_raw.txt files.",
                legacy.Length, _options.DataDirectory);
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
    /// Uploaded files are only a hand-off from the controller; the extracted text is kept by the
    /// document registry, so the original is removed whether ingestion succeeded or failed.
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
