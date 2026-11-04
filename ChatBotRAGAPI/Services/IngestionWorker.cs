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
    private readonly RagOptions _options;
    private readonly ILogger<IngestionWorker> _logger;

    public IngestionWorker(
        IngestionQueue queue,
        WebScraperService scraper,
        FileTextExtractor extractor,
        TextChunker chunker,
        IEmbeddingClient embeddingClient,
        IVectorStore vectorStore,
        IOptions<RagOptions> options,
        ILogger<IngestionWorker> logger)
    {
        _queue = queue;
        _scraper = scraper;
        _extractor = extractor;
        _chunker = chunker;
        _embeddingClient = embeddingClient;
        _vectorStore = vectorStore;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        await EnsureVectorStoreAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var job = await _queue.DequeueAsync(stoppingToken);
            _queue.MarkRunning(job.JobId);

            try
            {
                var text = job.SourceType == "url"
                    ? await _scraper.ScrapeTextAsync(job.SourceValue, stoppingToken)
                    : await _extractor.ExtractAsync(job.SourceValue, stoppingToken);

                var rawPath = Path.Combine(_options.DataDirectory, $"{job.JobId}_raw.txt");
                await File.WriteAllTextAsync(rawPath, text, stoppingToken);

                var chunks = _chunker.Chunk(text, _options.ChunkSize, _options.ChunkOverlap);
                foreach (var batch in chunks.Chunk(Math.Max(1, _options.EmbeddingBatchSize)))
                {
                    var vectors = await _embeddingClient.EmbedBatchAsync(batch, stoppingToken);
                    var points = batch
                        .Select((chunk, i) => new VectorPoint(ChunkId(chunk), vectors[i], chunk, job.SourceName))
                        .ToList();
                    await _vectorStore.UpsertAsync(points, stoppingToken);
                }

                _queue.MarkCompleted(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion failed for JobId {JobId}", job.JobId);
                _queue.MarkFailed(job.JobId, ex.Message);
            }
            finally
            {
                if (job.SourceType == "file") DeleteUpload(job.SourceValue);
            }
        }
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
    /// Content-derived id so re-ingesting identical text overwrites the existing point
    /// instead of adding a duplicate (which would crowd out other results in TopK).
    /// </summary>
    private static Guid ChunkId(string chunk)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(chunk));
        return new Guid(hash.AsSpan(0, 16));
    }
}