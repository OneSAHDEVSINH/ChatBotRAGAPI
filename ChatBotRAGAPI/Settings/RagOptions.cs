namespace ChatBotRAGAPI.Settings;

public sealed class RagOptions
{
    public string DataDirectory { get; set; } = "data";
    public bool UseInMemoryVectorStore { get; set; } = true;
    public bool UseLocalAiFallback { get; set; } = true; // true => no Ollama required
    public string QdrantBaseUrl { get; set; } = "http://localhost:6333";
    public string QdrantCollection { get; set; } = "knowledge";
    public string EmbeddingModel { get; set; } = "text-embedding-nomic:latest";
    public string ChatModel { get; set; } = "gpt-5-qwen3:latest";
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";
    public int ChunkSize { get; set; } = 100;
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
    /// 0.45 suits nomic-embed-text (relevant hits ~0.56-0.75, off-topic questions <= 0.41).
    /// </summary>
    public double MinScore { get; set; } = 0.45;

    /// <summary>MinScore used with UseLocalAiFallback, whose hashed bag-of-words scores are far lower (relevant ~0.1-0.35, noise <= 0.05).</summary>
    public double LocalMinScore { get; set; } = 0.08;

    public double EffectiveMinScore => UseLocalAiFallback ? LocalMinScore : MinScore;

    public int EmbeddingBatchSize { get; set; } = 32;   // chunks per embedding request / vector upsert

    /// <summary>Sends think=false to Ollama so reasoning models don't spend MaxGenerateTokens on hidden thinking.</summary>
    public bool DisableThinking { get; set; } = true;
}