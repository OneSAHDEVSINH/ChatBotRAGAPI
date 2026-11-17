using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Security;

/// <summary>
/// Microsoft Entra ID sign-in for the admin page (<c>Authentication:Entra</c>). Server-side: <c>/signin</c> redirects
/// to Microsoft, Microsoft posts back to <c>/signin-oidc</c>, and a cookie keeps the session (8 hours, sliding). The
/// admin page then calls the management API with that cookie; <see cref="ApiKeyAttribute"/> accepts a signed-in user
/// who has the <see cref="EntraOptions.AdminRole"/> app role, as well as the API key (for scripts). With Entra on, an
/// empty API key no longer leaves management open.
/// </summary>
public static class EntraSetup
{
    public const string CookieScheme = "AdminCookie";
    public const string OidcScheme = "Entra";

    /// <summary>
    /// Cookie-authenticated changes must carry this header with <see cref="CsrfValue"/>. Another site can make a
    /// browser send the cookie, but not a custom header (that needs CORS, which this app never grants).
    /// </summary>
    public const string CsrfHeader = "X-Requested-With";
    public const string CsrfValue = "knowledge-chat-admin";

    public static void Add(WebApplicationBuilder builder, EntraOptions entra)
    {
        // Keys that encrypt the sign-in cookie: kept with the data, so a restart doesn't sign everyone out.
        var dataDirectory = Path.GetFullPath(builder.Configuration["Rag:DataDirectory"] ?? new RagOptions().DataDirectory,
            builder.Environment.ContentRootPath);
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDirectory, "keys")))
            .SetApplicationName("ChatBotRAGAPI");

        var auth = builder.Services.AddAuthentication(o =>
        {
            o.DefaultScheme = CookieScheme;
            o.DefaultChallengeScheme = OidcScheme;
        });
        if (!entra.Enabled) return;

        entra.Validate();
        auth.AddCookie(CookieScheme, o =>
        {
            o.Cookie.Name = "__Host-KnowledgeChatAdmin";
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            // API calls get status codes, never a redirect to a login page.
            o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        })
        .AddOpenIdConnect(OidcScheme, o =>
        {
            o.Authority = $"{entra.Instance.TrimEnd('/')}/{entra.TenantId}/v2.0";
            o.ClientId = entra.ClientId;
            o.ClientSecret = entra.ClientSecret;
            o.ResponseType = "code";
            o.UsePkce = true;
            o.CallbackPath = "/signin-oidc";
            o.SignedOutCallbackPath = "/signout-callback-oidc";
            o.SignInScheme = CookieScheme;
            o.SaveTokens = false;
            o.MapInboundClaims = false;
            o.TokenValidationParameters.NameClaimType = "preferred_username";
            o.TokenValidationParameters.RoleClaimType = "roles";
        });
    }

    public static void Map(WebApplication app, EntraOptions entra)
    {
        app.MapGet("/signin", (HttpContext http) => entra.Enabled
            ? Results.Challenge(new AuthenticationProperties { RedirectUri = $"{http.Request.PathBase}/admin.html" }, [OidcScheme])
            : Results.NotFound("Entra sign-in is not configured (Authentication:Entra)."))
            .ExcludeFromDescription();

        app.MapGet("/signout", (HttpContext http) => entra.Enabled
            ? Results.SignOut(new AuthenticationProperties { RedirectUri = $"{http.Request.PathBase}/admin.html" }, [CookieScheme, OidcScheme])
            : Results.NotFound())
            .ExcludeFromDescription();

        // What the admin page needs to show the right sign-in controls. Open: it reveals only the caller's own name.
        app.MapGet("/api/me", (HttpContext http) => Results.Ok(new SignInStatus(
            entra.Enabled, http.User.Identity?.IsAuthenticated == true, http.User.Identity?.Name, IsAdmin(http))))
            .ExcludeFromDescription();
    }

    /// <summary>A signed-in Entra user with the admin app role.</summary>
    public static bool IsAdmin(HttpContext http)
    {
        var entra = http.RequestServices.GetRequiredService<IOptions<EntraOptions>>().Value;
        return entra.Enabled && http.User.Identity?.IsAuthenticated == true && http.User.IsInRole(entra.AdminRole);
    }

    public sealed record SignInStatus(bool Entra, bool SignedIn, string? Name, bool Admin);
}
