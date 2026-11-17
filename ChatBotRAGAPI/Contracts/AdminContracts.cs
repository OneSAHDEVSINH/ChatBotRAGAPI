namespace ChatBotRAGAPI.Contracts;

/// <summary>One management action.</summary>
/// <param name="Actor">The signed-in user, "api key", or "no key configured" when management is open.</param>
/// <param name="Ip">The client address (after ForwardedHeaders, when enabled).</param>
/// <param name="Action">controller.action, e.g. documents.delete; auth.rejected for a wrong or missing API key.</param>
/// <param name="Target">What it acted on: file name, URL, source, id or query.</param>
/// <param name="Status">The HTTP status it ended with.</param>
public sealed record AuditEntry(DateTimeOffset At, string Actor, string? Ip, string Action, string? Target, int Status);

/// <summary>What a backup holds, stored as manifest.json inside the zip.</summary>
public sealed record BackupManifest(string Format, int Version, DateTimeOffset CreatedAt, int Documents, string EmbeddingModel);

/// <param name="Documents">Documents restored; each is re-indexed by a "reindex" job (see the jobs list).</param>
/// <param name="ReindexJobs">Jobs queued to rebuild the search index from the restored texts.</param>
public sealed record RestoreResponse(int Documents, int ReindexJobs, DateTimeOffset BackupCreatedAt);
