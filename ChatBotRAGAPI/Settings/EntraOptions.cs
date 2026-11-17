namespace ChatBotRAGAPI.Settings;

/// <summary>
/// Microsoft Entra ID sign-in for the admin page (<c>Authentication:Entra</c>). Off while TenantId and ClientId are
/// empty. Needs an app registration in your tenant (free): a web redirect URI <c>https://your-host/signin-oidc</c>,
/// a client secret, and an app role (default "Chat.Admin") assigned to the people who may manage the bot.
/// Supply ClientSecret via environment (<c>Authentication__Entra__ClientSecret</c>) or user secrets, never appsettings.
/// </summary>
public sealed class EntraOptions
{
    /// <summary>Directory (tenant) id, or a verified domain such as contoso.onmicrosoft.com.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Application (client) id of the app registration.</summary>
    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>The sign-in host; change only for a national cloud (e.g. https://login.microsoftonline.us/).</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>The app role (value) a user needs to manage the bot. Signed-in users without it get 403.</summary>
    public string AdminRole { get; set; } = "Chat.Admin";

    public bool Enabled => !string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>Half-configured sign-in fails at startup with what is missing, instead of failing at the first login.</summary>
    public void Validate()
    {
        var missing = new[] { (nameof(TenantId), TenantId), (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret), (nameof(AdminRole), AdminRole) }
            .Where(s => string.IsNullOrWhiteSpace(s.Item2)).Select(s => $"Authentication:Entra:{s.Item1}").ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"Entra sign-in is partly configured; also set {string.Join(", ", missing)}.");
        if (!Uri.TryCreate(Instance, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new InvalidOperationException("Authentication:Entra:Instance must be an https URL.");
    }
}
