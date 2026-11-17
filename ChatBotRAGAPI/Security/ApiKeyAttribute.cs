using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ChatBotRAGAPI.Security;

/// <summary>
/// Management endpoints: the <c>X-Api-Key</c> header must match <see cref="SecurityOptions.ApiKey"/>, or the caller is
/// signed in with Entra ID and has the admin role (<see cref="EntraSetup"/>; changes then also need the
/// <see cref="EntraSetup.CsrfHeader"/> header). With neither a key nor Entra configured, the endpoint is open (see
/// the startup warning in Program.cs).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var http = context.HttpContext;
        var expected = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey;
        var entra = http.RequestServices.GetRequiredService<IOptions<EntraOptions>>().Value;
        var provided = http.Request.Headers[HeaderName].ToString();

        if (!string.IsNullOrEmpty(expected) && Matches(provided, expected)) return;

        if (EntraSetup.IsAdmin(http))
        {
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || http.Request.Headers[EntraSetup.CsrfHeader] == EntraSetup.CsrfValue)
                return;
            Reject(context, new ObjectResult($"Changes made with a sign-in need the {EntraSetup.CsrfHeader} header.") { StatusCode = StatusCodes.Status403Forbidden },
                "(sign-in without the CSRF header)");
            return;
        }

        if (string.IsNullOrEmpty(expected) && !entra.Enabled) return; // nothing configured: open, as warned at startup

        if (http.User.Identity?.IsAuthenticated == true)
        {
            Reject(context, new ObjectResult($"Signed in as {http.User.Identity.Name}, who does not have the {entra.AdminRole} role.")
                { StatusCode = StatusCodes.Status403Forbidden }, "(signed in without the admin role)");
            return;
        }

        Reject(context, new UnauthorizedObjectResult(entra.Enabled
                ? $"Sign in (/signin) or send a valid {HeaderName} header."
                : $"A valid {HeaderName} header is required."),
            provided.Length == 0 ? "(no key)" : "(wrong key)");
    }

    private static void Reject(AuthorizationFilterContext context, ObjectResult result, string why)
    {
        context.Result = result;
        var http = context.HttpContext;
        http.RequestServices.GetRequiredService<Services.AuditLog>().Record(http, "auth.rejected",
            $"{http.Request.Method} {http.Request.Path} {why}", result.StatusCode ?? StatusCodes.Status401Unauthorized);
    }

    /// <summary>True when Security:ApiKey is set and the request carries it (so an unset key never counts as "valid").</summary>
    public static bool IsValidManagementKey(HttpContext http)
    {
        var expected = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey;
        return !string.IsNullOrEmpty(expected) && Matches(http.Request.Headers[HeaderName].ToString(), expected);
    }

    /// <summary>Constant-time comparison, so response timing doesn't reveal how much of the key was right.</summary>
    public static bool Matches(string provided, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}
