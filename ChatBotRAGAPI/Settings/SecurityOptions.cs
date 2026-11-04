namespace ChatBotRAGAPI.Settings;

public sealed class SecurityOptions
{
    /// <summary>
    /// Key required in the <c>X-Api-Key</c> header for ingestion and document management.
    /// Empty = those endpoints are open (a warning is logged at startup). Set it via environment
    /// (<c>Security__ApiKey</c>) or user secrets; never commit it.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>Per-client-IP fixed-window limit applied to every request.</summary>
public sealed class RateLimitOptions
{
    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 10;
    public int QueueLimit { get; set; } = 10;   // requests waiting for the next window before 429s start
}
