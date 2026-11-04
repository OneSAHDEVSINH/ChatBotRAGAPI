namespace ChatBotRAGAPI.Contracts;

public sealed record ScrapeRequest(string Url);
/// <param name="History">
/// Earlier turns of this conversation, oldest first (optional). The server keeps no conversation state;
/// clients send back the previous questions and answers so follow-ups like "and the Pro plan?" make sense.
/// </param>
public sealed record AskRequest(string Question, IReadOnlyList<ChatMessage>? History = null);

/// <param name="Role">"user" or "assistant".</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>SSE payloads of /api/chat/ask/stream: "sources" (list of ContextSource), then "delta"*, then "done" or "error".</summary>
public sealed record StreamDelta(string Text);
public sealed record StreamDone(string Answer);
public sealed record StreamError(string Message);

/// <param name="Context">Retrieved chunk texts, in rank order (kept for backward compatibility).</param>
/// <param name="Sources">Where each context chunk came from; <c>Id</c> matches the [n] citations in the answer.</param>
public sealed record AskResponse(string Answer, IReadOnlyList<string> Context, IReadOnlyList<ContextSource> Sources);
/// <param name="Score">Cosine similarity to the question; null when the passage was found by keyword search only.</param>
/// <param name="Match">Why the passage was retrieved: "semantic", "keyword" or "both".</param>
public sealed record ContextSource(int Id, string Source, double? Score, string Match);

public sealed record EnqueueResponse(Guid JobId, string Status);
public sealed record JobStatusResponse(Guid JobId, string Status, string? Message);

/// <param name="SourceValue">What the worker reads: a URL, or the saved upload path.</param>
/// <param name="SourceName">Human-readable origin stored with each chunk: the URL or the original file name.</param>
public sealed record IngestionJob(Guid JobId, string SourceType, string SourceValue, string SourceName);

/// <param name="Source">The document key: original file name or URL. Re-ingesting the same source replaces it.</param>
public sealed record DocumentInfo(string Source, string SourceType, int ChunkCount, DateTimeOffset IngestedAt);

public sealed record VectorPoint(Guid Id, float[] Vector, string Text, string Source);
public sealed record SearchHit(string Text, string Source, double Score);
