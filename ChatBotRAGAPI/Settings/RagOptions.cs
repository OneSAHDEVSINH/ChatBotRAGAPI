namespace ChatBotRAGAPI.Settings;

public sealed class RagOptions
{
    public string DataDirectory { get; set; } = "data";
    public bool UseInMemoryVectorStore { get; set; } = true;
    public bool UseLocalAiFallback { get; set; } = false; // true => no Ollama required (hashed embeddings, extractive answers)
    public string QdrantBaseUrl { get; set; } = "http://127.0.0.1:6333"; // not "localhost": on Windows .NET tries ::1 first and each refused address costs ~2 s
    public string QdrantCollection { get; set; } = "knowledge";
    public string EmbeddingModel { get; set; } = "text-embedding-nomic:latest";
    public string ChatModel { get; set; } = "gpt-5-qwen3:latest";

    /// <summary>
    /// Ollama vision model that reads scanned PDF pages (pages with no text layer), e.g. "qwen2.5vl:3b" or
    /// "granite3.2-vision". Empty = OCR off: a PDF with no text layer fails with an explanation.
    /// </summary>
    public string OcrModel { get; set; } = "";

    /// <summary>Token budget for one page's transcription (a dense page is ~600-1000 tokens).</summary>
    public int OcrMaxTokens { get; set; } = 2048;
    public string OllamaBaseUrl { get; set; } = "http://127.0.0.1:11434";

    /// <summary>
    /// Who writes answers: "Ollama" (default, local), "OpenAI" (OpenAI, Azure OpenAI or any OpenAI-compatible
    /// server, via Microsoft.Extensions.AI) or "Anthropic" (Claude). ChatModel names the model for that provider.
    /// </summary>
    public string ChatProvider { get; set; } = "Ollama";

    /// <summary>
    /// Who embeds text: "Ollama" (default) or "OpenAI". Anthropic has no embeddings API. Changing the embedding
    /// model changes the vector space: set EmbeddingDimensions to its size and recalibrate MinScore.
    /// </summary>
    public string EmbeddingProvider { get; set; } = "Ollama";

    public OpenAiProviderOptions OpenAI { get; set; } = new();
    public AnthropicProviderOptions Anthropic { get; set; } = new();
    public int ChunkSize { get; set; } = 700;
    public int ChunkOverlap { get; set; } = 80;
    public int TopK { get; set; } = 3;
    public int EmbeddingDimensions { get; set; } = 768;
    public int MaxGenerateTokens { get; set; } = 180;
    public int NumCtx { get; set; } = 2048;
    public string KeepAlive { get; set; } = "10m";
    public int JobStatusRetentionMinutes { get; set; } = 60; // finished job statuses are dropped after this

    /// <summary>
    /// Hits scoring below this cosine similarity are not passed to the answer generator; 0 disables the filter.
    /// Scores are not comparable across embedding models, so recalibrate when changing EmbeddingModel.
    /// 0.45 suits nomic-embed-text (relevant hits ~0.56-0.75, off-topic questions at most 0.41).
    /// </summary>
    public double MinScore { get; set; } = 0.45;

    /// <summary>MinScore used with UseLocalAiFallback, whose hashed bag-of-words scores are far lower (relevant ~0.14-0.45; unrelated usually 0, hash collisions up to ~0.15).</summary>
    public double LocalMinScore { get; set; } = 0.08;

    public double EffectiveMinScore => UseLocalAiFallback ? LocalMinScore : MinScore;

    /// <summary>Combine vector search with BM25 keyword search (reciprocal rank fusion). False = vector search only.</summary>
    public bool HybridSearch { get; set; } = true;

    /// <summary>
    /// A passage found only by keyword search (its cosine is below MinScore) is used when it contains at least
    /// this share of the question's terms (stopwords excluded). 1.0 = every term: this admits terse exact-term
    /// queries ("Redis caching", "Q3-2026") without admitting passages that share generic words — at 0.5,
    /// "What is the best way to learn the guitar?" matched a paper containing "best", "way" and "learn".
    /// Keyword matches still re-rank passages the semantic search admitted, whatever their coverage.
    /// </summary>
    public double MinKeywordCoverage { get; set; } = 1.0;

    /// <summary>Candidates taken from each search before fusion; the best TopK of the fused list are used.</summary>
    public int SearchCandidates { get; set; } = 20;

    public int EmbeddingBatchSize { get; set; } = 32;   // chunks per embedding request / vector upsert

    /// <summary>
    /// Ingestion waits between embedding batches while a chat request is running, so answers don't share the
    /// model's CPU with a crawl. Each wait is capped at ChatPriorityMaxWaitSeconds so ingestion still progresses.
    /// </summary>
    public bool PrioritizeChat { get; set; } = true;

    /// <summary>Feedback log cap: once data/feedback.jsonl reaches this size, new ratings are refused (the endpoint is open to users).</summary>
    public int FeedbackMaxFileMB { get; set; } = 20;
    public int ChatPriorityMaxWaitSeconds { get; set; } = 30;

    /// <summary>Largest maxPages a scrape request may ask for (crawling a site from the given page).</summary>
    public int MaxCrawlPages { get; set; } = 50;

    /// <summary>Pause between page requests while crawling, so a crawl doesn't hammer the site.</summary>
    public int CrawlDelayMilliseconds { get; set; } = 500;

    /// <summary>Retries of transient Ollama/Qdrant failures (connection refused, 5xx, 408, 429); 0 disables.</summary>
    public int HttpRetryAttempts { get; set; } = 2;

    /// <summary>First retry delay; doubles per attempt with jitter (~0.5 s, ~1 s by default). Kept short: a chat request waits through all attempts, and on Windows each refused connection already costs ~2 s.</summary>
    public double HttpRetryBaseDelaySeconds { get; set; } = 0.5;

    /// <summary>Only the most recent this-many history messages are used (prompt size grows with each turn).</summary>
    public int MaxHistoryMessages { get; set; } = 6;

    /// <summary>Sends think=false to Ollama so reasoning models don't spend MaxGenerateTokens on hidden thinking.</summary>
    public bool DisableThinking { get; set; } = true;
}

/// <summary>Rag:OpenAI. Also covers Azure OpenAI and OpenAI-compatible servers through Endpoint.</summary>
public sealed class OpenAiProviderOptions
{
    /// <summary>Empty = the OPENAI_API_KEY environment variable. Supply it via environment or user secrets, never appsettings.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>
    /// Empty = api.openai.com. Azure OpenAI: "https://{resource}.openai.azure.com/openai/v1/"; any OpenAI-compatible
    /// server, e.g. Ollama's "http://127.0.0.1:11434/v1".
    /// </summary>
    public string Endpoint { get; set; } = "";

    /// <summary>Answer budget. Reasoning models count their reasoning in it, so it is far above Ollama's MaxGenerateTokens.</summary>
    public int MaxOutputTokens { get; set; } = 16000;
}

/// <summary>Rag:Anthropic (Claude).</summary>
public sealed class AnthropicProviderOptions
{
    /// <summary>Empty = the SDK's own lookup (ANTHROPIC_API_KEY, then an `ant auth login` profile). Never put it in appsettings.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Answer budget. Current Claude models think before answering and that counts against it.</summary>
    public int MaxTokens { get; set; } = 16000;
}
