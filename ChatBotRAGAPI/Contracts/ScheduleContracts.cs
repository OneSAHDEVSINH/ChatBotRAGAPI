namespace ChatBotRAGAPI.Contracts;

/// <param name="Url">The start page to crawl (public http/https).</param>
/// <param name="MaxPages">Pages per run (1 to Rag:MaxCrawlPages).</param>
/// <param name="RemoveMissing">Delete the site's pages a run no longer finds (needs MaxPages above 1).</param>
/// <param name="IntervalHours">Hours between runs (1 to 720). The first run starts within a minute of creating it.</param>
/// <param name="KnowledgeBase">Which knowledge base the pages go into; omitted = "default".</param>
public sealed record CrawlScheduleRequest(string Url, int MaxPages = 20, bool RemoveMissing = true, int IntervalHours = 24, string? KnowledgeBase = null);

public sealed record CrawlSchedule(
    Guid Id, string Url, int MaxPages, bool RemoveMissing, int IntervalHours,
    DateTimeOffset CreatedAt, DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt, Guid? LastJobId, string KnowledgeBase = "default");

/// <param name="LastJobStatus">Status of the most recent run's ingestion job (Queued, Running, Completed, Failed), if still known.</param>
/// <param name="LastJobMessage">That job's message, e.g. "Ingested 18 page(s). Removed 2 page(s) no longer on the site."</param>
public sealed record CrawlScheduleInfo(
    Guid Id, string Url, int MaxPages, bool RemoveMissing, int IntervalHours,
    DateTimeOffset CreatedAt, DateTimeOffset NextRunAt, DateTimeOffset? LastRunAt, Guid? LastJobId,
    string? LastJobStatus, string? LastJobMessage, string KnowledgeBase = "default");
