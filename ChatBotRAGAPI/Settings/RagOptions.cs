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
}