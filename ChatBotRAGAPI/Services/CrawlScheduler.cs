using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Re-crawls sites on a timetable so their documents stay current without anyone pressing a button. Schedules live
/// in <c>{DataDirectory}/schedules.json</c>; each run is an ordinary crawl job (visible in the jobs list, with
/// removeMissing when set), started at most once per interval and never while the previous run is still going.
/// </summary>
public sealed class CrawlScheduler
{
    public const int MaxSchedules = 20;
    public const int MaxIntervalHours = 720;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly IngestionQueue _queue;
    private readonly RagOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<CrawlScheduler>? _logger;
    private readonly string _path;
    private readonly object _sync = new();
    private readonly List<CrawlSchedule> _schedules;

    public CrawlScheduler(IngestionQueue queue, IOptions<RagOptions> options, TimeProvider? time = null, ILogger<CrawlScheduler>? logger = null)
    {
        _queue = queue;
        _options = options.Value;
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Directory.CreateDirectory(_options.DataDirectory);
        _path = Path.Combine(_options.DataDirectory, "schedules.json");
        _schedules = Load();
    }

    public IReadOnlyList<CrawlScheduleInfo> List()
    {
        lock (_sync) return _schedules.OrderBy(s => s.CreatedAt).Select(Describe).ToList();
    }

    /// <returns>The new schedule, or the reason it was refused.</returns>
    public (CrawlScheduleInfo? Schedule, string? Error) Add(CrawlScheduleRequest request)
    {
        if (ScrapeTarget.Validate(request.Url, request.MaxPages, request.RemoveMissing, _options.MaxCrawlPages, out var uri) is { } error)
            return (null, error);
        if (request.IntervalHours is < 1 or > MaxIntervalHours)
            return (null, $"intervalHours must be between 1 and {MaxIntervalHours}.");

        lock (_sync)
        {
            if (_schedules.Count >= MaxSchedules) return (null, $"At most {MaxSchedules} schedules.");
            var now = _time.GetUtcNow();
            // Due at once: the first run proves the address works instead of failing silently a day later.
            var schedule = new CrawlSchedule(Guid.NewGuid(), uri!.ToString(), request.MaxPages, request.RemoveMissing,
                request.IntervalHours, now, now, null, null);
            _schedules.Add(schedule);
            Save();
            return (Describe(schedule), null);
        }
    }

    public bool Remove(Guid id)
    {
        lock (_sync)
        {
            if (_schedules.RemoveAll(s => s.Id == id) == 0) return false;
            Save();
            return true;
        }
    }

    /// <summary>Starts a run now; the next scheduled run moves to a full interval after it.</summary>
    /// <returns>The job id, null for an unknown schedule, or Guid.Empty when its previous run is still going.</returns>
    public Guid? RunNow(Guid id)
    {
        lock (_sync)
        {
            var index = _schedules.FindIndex(s => s.Id == id);
            if (index < 0) return null;
            return Start(index) ?? Guid.Empty;
        }
    }

    /// <summary>Starts every schedule that is due. Called about once a minute by <see cref="CrawlSchedulerService"/>.</summary>
    /// <returns>How many runs were started.</returns>
    public int RunDue()
    {
        lock (_sync)
        {
            var now = _time.GetUtcNow();
            var started = 0;
            for (var i = 0; i < _schedules.Count; i++)
            {
                if (_schedules[i].NextRunAt > now) continue;
                if (Start(i) is not null) started++;
            }
            return started;
        }
    }

    /// <summary>Enqueues the crawl unless the previous run is still Queued or Running. Caller holds the lock.</summary>
    private Guid? Start(int index)
    {
        var schedule = _schedules[index];
        if (schedule.LastJobId is { } last && _queue.GetStatus(last).Status is "Queued" or "Running")
            return null;

        var now = _time.GetUtcNow();
        var jobId = _queue.Enqueue("url", schedule.Url, schedule.Url, schedule.MaxPages, schedule.RemoveMissing);
        _schedules[index] = schedule with
        {
            LastRunAt = now, LastJobId = jobId, NextRunAt = now + TimeSpan.FromHours(schedule.IntervalHours),
        };
        Save();
        _logger?.LogInformation("Scheduled crawl of {Url} started as job {JobId}", schedule.Url, jobId);
        return jobId;
    }

    private CrawlScheduleInfo Describe(CrawlSchedule s)
    {
        var (status, message) = s.LastJobId is { } id ? _queue.GetStatus(id) : ("", null);
        return new CrawlScheduleInfo(s.Id, s.Url, s.MaxPages, s.RemoveMissing, s.IntervalHours, s.CreatedAt, s.NextRunAt,
            s.LastRunAt, s.LastJobId, status is "" or "NotFound" ? null : status, message);
    }

    private List<CrawlSchedule> Load()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<CrawlSchedule>>(File.ReadAllText(_path), Json) ?? [];
        }
        catch (JsonException ex)
        {
            var backup = $"{_path}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmss}";
            File.Move(_path, backup);
            _logger?.LogError(ex, "Crawl schedules were unreadable; moved to {Backup} and starting with none", backup);
            return [];
        }
    }

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_schedules, Json));
        File.Move(temp, _path, overwrite: true);
    }
}

/// <summary>Wakes once a minute and starts the crawls that are due. Errors are logged; it never stops the host.</summary>
public sealed class CrawlSchedulerService(CrawlScheduler scheduler, TimeProvider time, ILogger<CrawlSchedulerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do
        {
            try
            {
                scheduler.RunDue();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Starting scheduled crawls failed; trying again in a minute");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
