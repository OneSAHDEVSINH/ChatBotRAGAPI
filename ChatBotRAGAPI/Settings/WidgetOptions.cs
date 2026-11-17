namespace ChatBotRAGAPI.Settings;

/// <summary>
/// The embeddable chat widget (<c>Widget</c> section). Websites add <c>&lt;script src="https://bot.example.com/widget.js" defer&gt;</c>
/// and the chat opens in a frame on their page. Browsers only show that frame on sites listed here
/// (<c>frame-ancestors</c>), so an unlisted site can't wrap the chat in its own page.
/// </summary>
public sealed class WidgetOptions
{
    /// <summary>Origins allowed to embed the chat, e.g. "https://www.example.com" (scheme and host, optional port, no path). Empty = nowhere.</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>The <c>frame-ancestors</c> sources for the chat page, after checking every origin; throws on an invalid one.</summary>
    public string FrameAncestors()
    {
        var sources = new List<string> { "'self'" };
        foreach (var origin in AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException(
                    $"Widget:AllowedOrigins contains '{origin}'; use an origin such as https://www.example.com (no path).");
            }
            sources.Add(uri.GetLeftPart(UriPartial.Authority));
        }
        return string.Join(' ', sources);
    }
}
