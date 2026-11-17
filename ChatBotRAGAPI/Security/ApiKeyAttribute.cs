using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

namespace ChatBotRAGAPI.Security;

/// <summary>
/// Requires the <c>X-Api-Key</c> header to match <see cref="SecurityOptions.ApiKey"/>. When no key is
/// configured the endpoint is open (see the startup warning in Program.cs).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class ApiKeyAttribute : Attribute, IAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey;
        if (string.IsNullOrEmpty(expected)) return;

        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (Matches(provided, expected)) return;

        context.Result = new UnauthorizedObjectResult($"A valid {HeaderName} header is required.");
        var http = context.HttpContext;
        http.RequestServices.GetRequiredService<Services.AuditLog>().Record(http, "auth.rejected",
            $"{http.Request.Method} {http.Request.Path}{(provided.Length == 0 ? " (no key)" : " (wrong key)")}", StatusCodes.Status401Unauthorized);
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
