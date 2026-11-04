namespace ChatBotRAGAPI.Contracts;

public sealed record ScrapeRequest(string Url);
public sealed record AskRequest(string Question);
public sealed record AskResponse(string Answer, IReadOnlyList<string> Context);
public sealed record EnqueueResponse(Guid JobId, string Status);
public sealed record JobStatusResponse(Guid JobId, string Status, string? Message);

public sealed record IngestionJob(Guid JobId, string SourceType, string SourceValue);

public sealed record SearchHit(string Text, double Score);