using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

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
        await _vectorStore.EnsureCollectionAsync(stoppingToken);

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
                foreach (var chunk in chunks)
                {
                    var vec = await _embeddingClient.EmbedAsync(chunk, stoppingToken);
                    await _vectorStore.UpsertAsync(Guid.NewGuid(), vec, chunk, job.SourceValue, stoppingToken);
                }

                _queue.MarkCompleted(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ingestion failed for JobId {JobId}", job.JobId);
                _queue.MarkFailed(job.JobId, ex.Message);
            }
        }
    }
}