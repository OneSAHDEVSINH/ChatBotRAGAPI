using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace ChatBotRAGAPI.Tests;

/// <summary>
/// Stands in for a completed Microsoft sign-in: X-Test-User names the user and X-Test-Roles lists their app roles,
/// exactly as the real cookie would carry them (name = preferred_username, roles = "roles" claims).
/// </summary>
internal sealed class FakeSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "FakeEntra";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers["X-Test-User"].ToString();
        if (user.Length == 0) return Task.FromResult(AuthenticateResult.NoResult());

        var claims = new List<Claim> { new("preferred_username", user) };
        claims.AddRange(Request.Headers["X-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(r => new Claim("roles", r)));
        var identity = new ClaimsIdentity(claims, SchemeName, "preferred_username", "roles");
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

public sealed class EntraTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string> Entra = new()
    {
        ["Authentication:Entra:TenantId"] = "contoso.onmicrosoft.com",
        ["Authentication:Entra:ClientId"] = "11111111-2222-3333-4444-555555555555",
        ["Authentication:Entra:ClientSecret"] = "secret",
    };

    private readonly RagApiFactory _factory;

    public EntraTests() : this(Entra) { }

    private EntraTests(Dictionary<string, string> settings)
    {
        _factory = new RagApiFactory(settings: settings, services: s =>
        {
            s.AddAuthentication().AddScheme<AuthenticationSchemeOptions, FakeSignInHandler>(FakeSignInHandler.SchemeName, null);
            s.PostConfigure<AuthenticationOptions>(o => o.DefaultAuthenticateScheme = FakeSignInHandler.SchemeName);
            // No network: the sign-in redirect is built from this instead of Microsoft's discovery document.
            s.PostConfigure<OpenIdConnectOptions>(EntraSetup.OidcScheme, o =>
            {
                o.Configuration = new OpenIdConnectConfiguration
                {
                    AuthorizationEndpoint = "https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/authorize",
                    EndSessionEndpoint = "https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/logout",
                    Issuer = "https://login.microsoftonline.com/contoso/v2.0",
                };
                o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(o.Configuration);
                o.Backchannel = new HttpClient(new StubHttpHandler((r, _) => throw new InvalidOperationException($"No network in tests: {r.RequestUri}")));
            });
        });
    }

    public void Dispose() => _factory.Dispose();

    private HttpClient Client(string? user = null, string? roles = null, bool csrf = false)
    {
        var client = _factory.CreateClient(new() { AllowAutoRedirect = false });
        if (user is not null) client.DefaultRequestHeaders.Add("X-Test-User", user);
        if (roles is not null) client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        if (csrf) client.DefaultRequestHeaders.Add(EntraSetup.CsrfHeader, EntraSetup.CsrfValue);
        return client;
    }

    [Fact]
    public async Task Sign_in_redirects_to_Microsoft_with_PKCE_and_the_callback()
    {
        using var client = Client();
        var response = await client.GetAsync("/signin", Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        Assert.StartsWith("https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/authorize?", location);
        Assert.Contains("client_id=11111111-2222-3333-4444-555555555555", location);
        Assert.Contains("response_type=code", location);
        Assert.Contains("code_challenge_method=S256", location);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%2Fsignin-oidc", location);
    }

    [Fact]
    public async Task An_admin_can_manage_without_the_api_key_and_changes_need_the_csrf_header()
    {
        using var admin = Client("ana@contoso.com", "Chat.Admin");
        using var adminWithHeader = Client("ana@contoso.com", "Chat.Admin", csrf: true);

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/documents", Ct)).StatusCode);
        var create = new KnowledgeBaseRequest("shop");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/knowledge-bases", create, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await adminWithHeader.PostAsJsonAsync("/api/knowledge-bases", create, Ct)).StatusCode);

        var audit = await admin.GetFromJsonAsync<List<AuditEntry>>("/api/audit", Ct);
        Assert.Equal(("knowledgebases.create", "ana@contoso.com"), (audit![0].Action, audit[0].Actor));
        Assert.Equal("auth.rejected", audit[1].Action);
    }

    [Fact]
    public async Task Signed_in_users_without_the_role_and_anonymous_callers_are_refused()
    {
        using var visitor = Client("bob@contoso.com", "Some.Other.Role", csrf: true);
        using var anonymous = Client();

        var forbidden = await visitor.GetAsync("/api/documents", Ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Contains("Chat.Admin", await forbidden.Content.ReadAsStringAsync(Ct));

        // With Entra on, an empty API key no longer leaves management open.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/documents", Ct)).StatusCode);
    }

    [Fact]
    public async Task Me_reports_the_sign_in_state()
    {
        using var anonymous = Client();
        using var admin = Client("ana@contoso.com", "Chat.Admin");

        Assert.Equal(new EntraSetup.SignInStatus(true, false, null, false), await anonymous.GetFromJsonAsync<EntraSetup.SignInStatus>("/api/me", Ct));
        Assert.Equal(new EntraSetup.SignInStatus(true, true, "ana@contoso.com", true), await admin.GetFromJsonAsync<EntraSetup.SignInStatus>("/api/me", Ct));
    }

    [Fact]
    public async Task An_admin_may_chat_with_private_knowledge_bases()
    {
        using var admin = Client("ana@contoso.com", "Chat.Admin", csrf: true);
        await admin.PostAsJsonAsync("/api/knowledge-bases", new KnowledgeBaseRequest("internal", null, Private: true), Ct);
        using var visitor = Client();

        var ask = new AskRequest("Anything?", null, "internal");
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/chat/ask", ask, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await visitor.PostAsJsonAsync("/api/chat/ask", ask, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_api_key_keeps_working_alongside_sign_in()
    {
        using var factory = new RagApiFactory(settings: new Dictionary<string, string>(Entra) { ["Security:ApiKey"] = "k" });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "k");
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/knowledge-bases", new KnowledgeBaseRequest("shop"), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("Authentication:Entra:ClientSecret", "ClientSecret")]
    [InlineData("Authentication:Entra:ClientId", "ClientId")]
    public void Half_configured_sign_in_fails_at_startup(string removed, string named)
    {
        var settings = new Dictionary<string, string>(Entra) { [removed] = "" };
        using var factory = new RagApiFactory(settings: settings);
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(named, error.ToString());
    }

    [Fact]
    public async Task Without_Entra_there_is_no_sign_in()
    {
        using var factory = new RagApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/signin", Ct)).StatusCode);
        Assert.False((await client.GetFromJsonAsync<EntraSetup.SignInStatus>("/api/me", Ct))!.Entra);
    }
}
