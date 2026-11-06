using ChatBotRAGAPI.Services.Ai;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ChatBotRAGAPI.Services.Health;

/// <summary>Set by the ingestion worker once its vector store is usable; read by the readiness check.</summary>
public sealed class WorkerState
{
    private volatile bool _storeReady;
    public bool StoreReady { get => _storeReady; set => _storeReady = value; }
}

/// <summary>Can the vector store serve searches right now (Qdrant reachable, collection present)?</summary>
public sealed class VectorStoreHealthCheck(IVectorStore store, IOptions<RagOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var kind = options.Value.UseInMemoryVectorStore ? "in-memory" : "Qdrant";
        try
        {
            await store.PingAsync(ct);
            return HealthCheckResult.Healthy($"{kind} vector store is reachable.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy($"{kind} vector store is not reachable: {ex.Message}");
        }
    }
}

/// <summary>The ingestion worker waits for the vector store at startup; until then uploads queue up but don't run.</summary>
public sealed class IngestionWorkerHealthCheck(WorkerState state) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default) =>
        Task.FromResult(state.StoreReady
            ? HealthCheckResult.Healthy("Ingestion worker is running.")
            : HealthCheckResult.Degraded("Ingestion worker is still waiting for the vector store; new jobs will queue."));
}

/// <summary>
/// Can the configured models answer? For Ollama: the server responds and every model this app uses is pulled
/// (the commonest misconfiguration, which otherwise only shows up as a failed question). Cloud providers are not
/// called, so probes never spend tokens; their keys are checked at startup.
/// </summary>
public sealed class AiBackendHealthCheck(IHttpClientFactory http, IOptions<RagOptions> options) : IHealthCheck
{
    public const string HttpClientName = "ollama-health";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var o = options.Value;
        if (o.UseLocalAiFallback) return HealthCheckResult.Healthy("Local fallback: no model backend needed.");

        var chatOnOllama = o.ChatProvider.Equals(AiProviders.Ollama, StringComparison.OrdinalIgnoreCase);
        var needed = new List<string>();
        if (chatOnOllama) needed.Add(o.ChatModel);
        if (o.EmbeddingProvider.Equals(AiProviders.Ollama, StringComparison.OrdinalIgnoreCase)) needed.Add(o.EmbeddingModel);
        if (chatOnOllama && !string.IsNullOrWhiteSpace(o.OcrModel)) needed.Add(o.OcrModel);

        var cloud = new[] { o.ChatProvider, o.EmbeddingProvider }
            .Where(p => !p.Equals(AiProviders.Ollama, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var cloudNote = cloud.Count == 0 ? "" : $" {string.Join(" and ", cloud)} configured (not probed, so checks spend no tokens).";
        if (needed.Count == 0) return HealthCheckResult.Healthy(cloudNote.Trim());

        try
        {
            using var response = await http.CreateClient(HttpClientName).GetAsync("/api/tags", ct);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var pulled = json.RootElement.GetProperty("models").EnumerateArray()
                .Select(m => m.GetProperty("name").GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);

            var missing = needed.Distinct().Where(m => !pulled.Contains(m) && !pulled.Contains(m + ":latest")).ToList();
            return missing.Count == 0
                ? HealthCheckResult.Healthy($"Ollama is up with {string.Join(", ", needed.Distinct())}.{cloudNote}")
                : HealthCheckResult.Unhealthy($"Ollama is up but missing {string.Join(", ", missing)}: run {string.Join("; ", missing.Select(m => $"ollama pull {m}"))}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            return HealthCheckResult.Unhealthy($"Ollama is not reachable: {ex.Message}");
        }
    }
}

/// <summary>Writes a readiness report as JSON: overall status, then each check's status and description.</summary>
public static class HealthReportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs = (int)e.Value.Duration.TotalMilliseconds,
            }),
        }, Json));
    }
}
