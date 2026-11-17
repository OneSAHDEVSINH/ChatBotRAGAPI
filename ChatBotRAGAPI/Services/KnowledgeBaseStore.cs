using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Separate sets of documents that are searched separately: a public website bot and an internal staff bot, or one
/// per client. Every document, chunk, job, schedule and usage line carries a knowledge base id; chat searches only
/// the one asked for. "default" always exists (everything from before knowledge bases lives there). A private
/// knowledge base needs its chat key (header X-Chat-Key) to chat; only a SHA-256 of the key is stored, so a lost key
/// is replaced, not recovered. Kept in <c>{DataDirectory}/knowledge-bases.json</c>.
/// </summary>
public sealed partial class KnowledgeBaseStore
{
    public const string Default = "default";
    public const string ChatKeyHeader = "X-Chat-Key";
    public const int MaxKnowledgeBases = 50;

    private sealed record Stored(string Id, string Name, string? ChatKeyHash, DateTimeOffset CreatedAt);

    public enum Access { Allowed, NotFound, KeyRequired }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly ILogger<KnowledgeBaseStore>? _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, Stored> _bases = new(StringComparer.Ordinal);

    public KnowledgeBaseStore(IOptions<RagOptions> options, TimeProvider? time = null, ILogger<KnowledgeBaseStore>? logger = null)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        _path = Path.Combine(options.Value.DataDirectory, "knowledge-bases.json");
        _time = time ?? TimeProvider.System;
        _logger = logger;
        Reload();
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex IdPattern();

    /// <summary>A null or empty id means <see cref="Default"/>.</summary>
    public static string Normalize(string? id) => string.IsNullOrWhiteSpace(id) ? Default : id.Trim();

    public bool Exists(string id)
    {
        lock (_sync) return _bases.ContainsKey(id);
    }

    public IReadOnlyList<(string Id, string Name, bool Private, DateTimeOffset CreatedAt)> List()
    {
        lock (_sync)
            return _bases.Values.OrderBy(b => b.Id != Default).ThenBy(b => b.Id, StringComparer.Ordinal)
                .Select(b => (b.Id, b.Name, b.ChatKeyHash is not null, b.CreatedAt)).ToList();
    }

    /// <summary>Whether a chat request may use <paramref name="id"/>: public, or private with its key (constant-time compare).</summary>
    public Access CanChat(string id, string? chatKey)
    {
        Stored? kb;
        lock (_sync) kb = _bases.GetValueOrDefault(id);
        if (kb is null) return Access.NotFound;
        if (kb.ChatKeyHash is null) return Access.Allowed;
        return !string.IsNullOrEmpty(chatKey) && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(kb.ChatKeyHash), SHA256.HashData(Encoding.UTF8.GetBytes(chatKey)))
            ? Access.Allowed : Access.KeyRequired;
    }

    /// <returns>The new chat key for a private knowledge base (shown once), or the reason it was refused.</returns>
    public (string? ChatKey, string? Error) Create(string id, string? name, bool isPrivate)
    {
        if (!IdPattern().IsMatch(id ?? ""))
            return (null, "id must be 1-40 lower-case letters, digits or hyphens, starting with a letter or digit.");
        lock (_sync)
        {
            if (_bases.ContainsKey(id!)) return (null, $"Knowledge base '{id}' already exists.");
            if (_bases.Count >= MaxKnowledgeBases) return (null, $"At most {MaxKnowledgeBases} knowledge bases.");
            var key = isPrivate ? NewKey() : null;
            _bases[id!] = new Stored(id!, CleanName(name, id!), key is null ? null : Hash(key), _time.GetUtcNow());
            Save();
            return (key, null);
        }
    }

    /// <summary>Renames and/or switches public/private. Making it private (or <paramref name="newKey"/>) issues a new key.</summary>
    /// <returns>Found = false for an unknown id; ChatKey = the new key when one was issued.</returns>
    public (bool Found, string? ChatKey) Update(string id, string? name, bool? isPrivate, bool newKey)
    {
        lock (_sync)
        {
            if (!_bases.TryGetValue(id, out var kb)) return (false, null);
            string? key = null;
            var hash = kb.ChatKeyHash;
            if (isPrivate == false) hash = null;
            else if (newKey || (isPrivate == true && hash is null)) hash = Hash(key = NewKey());
            _bases[id] = kb with { Name = name is null ? kb.Name : CleanName(name, id), ChatKeyHash = hash };
            Save();
            return (true, key);
        }
    }

    /// <summary>Removes a knowledge base's entry; the caller makes sure it has no documents. "default" stays.</summary>
    public bool Remove(string id)
    {
        if (id == Default) return false;
        lock (_sync)
        {
            if (!_bases.Remove(id)) return false;
            Save();
            return true;
        }
    }

    /// <summary>Reads knowledge-bases.json again (startup, and after a restore replaced it).</summary>
    public void Reload()
    {
        lock (_sync)
        {
            _bases.Clear();
            if (File.Exists(_path))
            {
                try
                {
                    foreach (var kb in JsonSerializer.Deserialize<List<Stored>>(File.ReadAllText(_path), Json) ?? [])
                        if (IdPattern().IsMatch(kb.Id)) _bases[kb.Id] = kb;
                }
                catch (JsonException ex)
                {
                    var backup = $"{_path}.corrupt-{_time.GetUtcNow():yyyyMMddHHmmss}";
                    File.Move(_path, backup);
                    _logger?.LogError(ex, "Knowledge bases were unreadable; moved to {Backup}. Private ones are unusable until recreated", backup);
                }
            }
            _bases.TryAdd(Default, new Stored(Default, "Default", null, DateTimeOffset.MinValue));
        }
    }

    private static string CleanName(string? name, string id)
    {
        var clean = string.IsNullOrWhiteSpace(name) ? id : name.Trim();
        return clean.Length > 80 ? clean[..80] : clean;
    }

    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_bases.Values.Where(b => b.Id != Default || b.ChatKeyHash is not null || b.Name != "Default"), Json));
        File.Move(temp, _path, overwrite: true);
    }
}
