using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services.Ai;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// One line per chat question in <c>{DataDirectory}/usage.jsonl</c>: when, how it ended, how long it took and, unless
/// Rag:UsageStoreQuestions is off, the question itself. Feeds the admin page's usage section. Chat is open to
/// everyone, so the file is capped at UsageMaxFileMB; past it nothing more is logged (chat keeps working).
/// </summary>
public sealed class UsageLog
{
    public const string Answered = "answered", NoData = "nodata", Declined = "declined", Error = "error";
    private const int MaxQuestionChars = 300;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly bool _storeQuestions;
    private readonly FeedbackStore _feedback;
    private readonly TimeProvider _time;
    private readonly ILogger<UsageLog>? _logger;
    private readonly object _sync = new();
    private bool _warnedFull;

    public UsageLog(IOptions<RagOptions> options, FeedbackStore feedback, TimeProvider? time = null, ILogger<UsageLog>? logger = null)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "usage.jsonl");
        _maxBytes = Math.Max(1, options.Value.UsageMaxFileMB) * 1024L * 1024L;
        _storeQuestions = options.Value.UsageStoreQuestions;
        _feedback = feedback;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    /// <summary>How a finished answer is classified.</summary>
    public static string OutcomeOf(string answer) => answer.Trim() switch
    {
        RagService.NoDataAnswer => NoData,
        ChatClientAnswerGenerator.DeclinedAnswer => Declined,
        _ => Answered,
    };

    /// <summary>Never throws: losing a usage line must not fail a chat answer.</summary>
    public void Record(string question, string outcome, int sources, TimeSpan elapsed, bool followUp,
        string knowledgeBase = KnowledgeBaseStore.Default)
    {
        var text = question.Trim();
        if (text.Length > MaxQuestionChars) text = text[..MaxQuestionChars] + "…";
        var entry = new UsageEntry(_time.GetUtcNow(), outcome, _storeQuestions ? text : null, sources,
            Math.Round(elapsed.TotalSeconds, 2), followUp, knowledgeBase);
        try
        {
            lock (_sync)
            {
                if (File.Exists(_path) && new FileInfo(_path).Length >= _maxBytes)
                {
                    if (!_warnedFull) _logger?.LogWarning("Usage log {Path} reached Rag:UsageMaxFileMB; no more questions are logged", _path);
                    _warnedFull = true;
                    return;
                }
                File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Could not write the usage log");
        }
    }

    /// <summary>Totals, a row per UTC day and the most asked unanswered questions for the last <paramref name="days"/> days.</summary>
    /// <param name="knowledgeBase">Only this knowledge base's questions; null = all (ratings are never filtered: they carry no knowledge base).</param>
    public UsageReport Report(int days, string? knowledgeBase = null)
    {
        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        var first = today.AddDays(1 - days);
        var since = new DateTimeOffset(first.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var entries = Read().Where(e => e.At >= since
            && (knowledgeBase is null || (e.KnowledgeBase ?? KnowledgeBaseStore.Default) == knowledgeBase)).ToList();
        var ratings = _feedback.All().Where(f => f.At >= since).ToList();

        static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd");
        var byDay = entries.ToLookup(e => Day(e.At));
        var ratingsByDay = ratings.ToLookup(f => Day(f.At));

        var rows = Enumerable.Range(0, days).Select(i =>
        {
            var date = first.AddDays(i).ToString("yyyy-MM-dd");
            var d = byDay[date].ToList();
            var r = ratingsByDay[date].ToList();
            return new UsageDay(date, d.Count, d.Count(e => e.Outcome == Answered), d.Count(e => e.Outcome == NoData),
                d.Count(e => e.Outcome == Declined), d.Count(e => e.Outcome == Error),
                r.Count(f => f.Rating == "up"), r.Count(f => f.Rating == "down"));
        }).ToList();

        var unanswered = entries
            .Where(e => e.Outcome == NoData && !string.IsNullOrWhiteSpace(e.Question))
            .GroupBy(e => e.Question!.Trim().TrimEnd('?', '.', '!').ToLowerInvariant())
            .Select(g => new UsageQuestion(g.OrderByDescending(e => e.At).First().Question!, g.Count(), g.Max(e => e.At)))
            .OrderByDescending(q => q.Count).ThenByDescending(q => q.LastAskedAt)
            .Take(10).ToList();

        var answeredTimes = entries.Where(e => e.Outcome == Answered).Select(e => e.Seconds).ToList();
        return new UsageReport(first.ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"), entries.Count,
            rows.Sum(r => r.Answered), rows.Sum(r => r.NoData), rows.Sum(r => r.Declined), rows.Sum(r => r.Errors),
            rows.Sum(r => r.Helpful), rows.Sum(r => r.NotHelpful),
            answeredTimes.Count == 0 ? null : Math.Round(answeredTimes.Average(), 1), rows, unanswered);
    }

    private List<UsageEntry> Read()
    {
        string[] lines;
        lock (_sync) lines = File.Exists(_path) ? File.ReadAllLines(_path) : [];

        var entries = new List<UsageEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<UsageEntry>(line, Json) is { } e) entries.Add(e); }
            catch (JsonException) { /* a torn last line from a crash: skip it */ }
        }
        return entries;
    }
}
