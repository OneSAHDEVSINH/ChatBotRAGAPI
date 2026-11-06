using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Ingestion job statuses on disk (<c>{DataDirectory}/jobs.json</c>), so a client polling a job id, or the admin
/// page's job list, still gets an answer after a restart. Written atomically; an unreadable file is set aside.
/// </summary>
public sealed class JobStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly ILogger<JobStore>? _logger;

    public JobStore(IOptions<RagOptions> options, ILogger<JobStore>? logger = null)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "jobs.json");
        _logger = logger;
    }

    public IReadOnlyList<JobInfo> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<JobInfo>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (JsonException ex)
        {
            var backup = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(_path, backup);
            _logger?.LogWarning(ex, "Job status file was unreadable; moved to {Backup} and starting empty", backup);
            return [];
        }
    }

    public void Save(IReadOnlyCollection<JobInfo> jobs)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(jobs, Json));
        File.Move(temp, _path, overwrite: true);
    }
}
