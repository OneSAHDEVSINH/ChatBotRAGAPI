using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Security;
using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Who changed what: one line per management action in <c>{DataDirectory}/audit.jsonl</c> (uploads, crawls, deletes,
/// schedules, backups, restores, evaluations) and per rejected API key. When the file reaches AuditMaxFileMB it is
/// moved to <c>audit.1.jsonl</c> (replacing the previous one) and a new file starts, so the log never fills the disk
/// and the most recent history is always kept.
/// </summary>
public sealed class AuditLog
{
    /// <summary>Set by an action to name what it acted on (a file name, URL, source or id).</summary>
    public const string TargetItem = "audit.target";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly string _previousPath;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditLog>? _logger;
    private readonly object _sync = new();

    public AuditLog(IOptions<RagOptions> options, TimeProvider? time = null, ILogger<AuditLog>? logger = null)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "audit.jsonl");
        _previousPath = Path.Combine(options.Value.DataDirectory, "audit.1.jsonl");
        _maxBytes = Math.Max(1, options.Value.AuditMaxFileMB) * 1024L * 1024L;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>Never throws: a full disk must not turn a successful delete into an error.</summary>
    public void Record(HttpContext http, string action, string? target, int status)
    {
        var entry = new AuditEntry(_time.GetUtcNow(), Actor(http), http.Connection.RemoteIpAddress?.ToString(),
            action, Cut(target), status);
        try
        {
            lock (_sync)
            {
                if (File.Exists(_path) && new FileInfo(_path).Length >= _maxBytes) File.Move(_path, _previousPath, overwrite: true);
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not write the audit log entry {Action} {Target}", action, target);
        }
    }

    /// <summary>Newest first, from the current file and then the previous one.</summary>
    public IReadOnlyList<AuditEntry> List(int limit)
    {
        limit = Math.Clamp(limit, 1, 1000);
        var entries = new List<AuditEntry>();
        lock (_sync)
        {
            foreach (var path in new[] { _path, _previousPath })
            {
                if (!File.Exists(path)) continue;
                foreach (var line in File.ReadAllLines(path).Reverse())
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { if (JsonSerializer.Deserialize<AuditEntry>(line, Json) is { } e) entries.Add(e); }
                    catch (JsonException) { /* a torn last line from a crash */ }
                    if (entries.Count == limit) return entries;
                }
            }
        }
        return entries;
    }

    /// <summary>A signed-in user's name when there is one; otherwise how the request got in.</summary>
    public static string Actor(HttpContext http)
    {
        if (http.User.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }) return name;
        var key = http.RequestServices.GetRequiredService<IOptions<SecurityOptions>>().Value.ApiKey;
        return string.IsNullOrEmpty(key) ? "no key configured" : "api key";
    }

    private static string? Cut(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length > 500 ? text[..500] + "…" : text.Trim();
}

/// <summary>
/// Records every non-GET request to an <see cref="ApiKeyAttribute"/> endpoint (and GETs marked
/// <see cref="AuditReadAttribute"/>, such as a backup download) once it has run, with its status code.
/// </summary>
public sealed class AuditFilter(AuditLog audit) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var metadata = context.ActionDescriptor.EndpointMetadata;
        var audited = metadata.OfType<ApiKeyAttribute>().Any()
            && (!HttpMethods.IsGet(context.HttpContext.Request.Method) || metadata.OfType<AuditReadAttribute>().Any());
        if (!audited)
        {
            await next();
            return;
        }

        var executed = await next();
        var http = context.HttpContext;
        var status = executed.Exception is not null && !executed.ExceptionHandled
            ? StatusCodes.Status500InternalServerError
            : (executed.Result as IStatusCodeActionResult)?.StatusCode ?? http.Response.StatusCode;

        var action = context.ActionDescriptor is ControllerActionDescriptor d
            ? $"{d.ControllerName.ToLowerInvariant()}.{d.ActionName.ToLowerInvariant()}"
            : context.ActionDescriptor.DisplayName ?? "unknown";
        var target = http.Items.TryGetValue(AuditLog.TargetItem, out var t) ? t as string
            : context.RouteData.Values.TryGetValue("id", out var id) ? id?.ToString()
            : http.Request.QueryString.HasValue ? Uri.UnescapeDataString(http.Request.QueryString.Value!) : null;
        audit.Record(http, action, target, status);
    }
}

/// <summary>Audits this GET too (it reads everything, e.g. a backup download).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AuditReadAttribute : Attribute;
