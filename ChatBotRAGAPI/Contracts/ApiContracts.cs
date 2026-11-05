namespace ChatBotRAGAPI.Contracts;

/// <param name="Url">Public http(s) address of the page to ingest.</param>
/// <param name="MaxPages">
/// 1 (default) ingests just this page. More crawls the site from it: links on the same host and under the
/// same path are followed, breadth-first, up to this many pages (at most Rag:MaxCrawlPages). Each page becomes
/// its own document, named by its URL.
/// </param>
/// <param name="RemoveMissing">
/// With a crawl: afterwards, delete page documents under the same host and path that the crawl no longer found
/// (no longer linked, or answering 404/410). Skipped when the crawl stopped at maxPages, and pages that failed with
/// a temporary error are kept.
/// </param>
public sealed record ScrapeRequest(string Url, int? MaxPages = null, bool RemoveMissing = false);
/// <param name="History">
/// Earlier turns of this conversation, oldest first (optional). The server keeps no conversation state;
/// clients send back the previous questions and answers so follow-ups like "and the Pro plan?" make sense.
/// </param>
public sealed record AskRequest(string Question, IReadOnlyList<ChatMessage>? History = null);

/// <param name="Role">"user" or "assistant".</param>
public sealed record ChatMessage(string Role, string Content);

/// <summary>SSE payloads of /api/chat/ask/stream: "sources" (list of ContextSource), "context" (list of passage texts), then "delta"*, then "done" or "error".</summary>
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
/// <param name="MaxPages">For "url" jobs: pages to crawl starting from SourceValue (1 = that page only).</param>
public sealed record IngestionJob(Guid JobId, string SourceType, string SourceValue, string SourceName, int MaxPages = 1, bool RemoveMissing = false);

/// <param name="Source">The document key: original file name or URL. Re-ingesting the same source replaces it.</param>
public sealed record DocumentInfo(string Source, string SourceType, int ChunkCount, DateTimeOffset IngestedAt);

public sealed record VectorPoint(Guid Id, float[] Vector, string Text, string Source);
public sealed record SearchHit(string Text, string Source, double Score);
