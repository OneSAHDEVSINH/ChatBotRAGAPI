using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Answer ratings from the chat UI, one JSON object per line in <c>{DataDirectory}/feedback.jsonl</c> (append-only,
/// easy to grep or load elsewhere). The rating endpoint is open to users, so the file is capped at FeedbackMaxFileMB.
/// </summary>
public sealed class FeedbackStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly TimeProvider _time;
    private readonly object _sync = new();

    public FeedbackStore(IOptions<RagOptions> options, TimeProvider? time = null)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "feedback.jsonl");
        _maxBytes = Math.Max(1, options.Value.FeedbackMaxFileMB) * 1024L * 1024L;
        _time = time ?? TimeProvider.System;
    }

    /// <returns>The stored entry, or null when the log has reached its size cap.</returns>
    public FeedbackEntry? Add(FeedbackRequest request)
    {
        var entry = new FeedbackEntry(Guid.NewGuid(), _time.GetUtcNow(), request.Rating, request.Question.Trim(), request.Answer.Trim(),
            request.Sources ?? [], string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim());

        lock (_sync)
        {
            if (File.Exists(_path) && new FileInfo(_path).Length >= _maxBytes) return null;
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, Json) + "\n");
        }
        return entry;
    }

    /// <param name="rating">"up", "down", or null for both.</param>
    public FeedbackList List(string? rating, int limit)
    {
        string[] lines;
        lock (_sync) lines = File.Exists(_path) ? File.ReadAllLines(_path) : [];

        var all = new List<FeedbackEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try { if (JsonSerializer.Deserialize<FeedbackEntry>(line, Json) is { } e) all.Add(e); }
            catch (JsonException) { /* a torn last line from a crash: skip it */ }
        }

        var entries = all.Where(e => rating is null || e.Rating == rating)
            .OrderByDescending(e => e.At).Take(Math.Clamp(limit, 1, 500)).ToList();
        return new FeedbackList(all.Count(e => e.Rating == "up"), all.Count(e => e.Rating == "down"), entries);
    }
}
