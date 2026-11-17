using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// A backup is a zip of the data directory's durable state: every document (registry, texts and cached embeddings),
/// the crawl schedules, ratings, usage and audit logs, plus a manifest. It does not contain the vector store: a
/// restore puts the files back and re-indexes every document through the normal "reindex" jobs, which reuse the
/// cached embeddings when the embedding model is unchanged, so it works for the in-memory store and Qdrant alike and
/// can move a knowledge base to another server.
/// </summary>
public sealed partial class BackupService
{
    public const string Format = "chatbotrag-backup";
    public const int Version = 1;

    /// <summary>Files outside documents/ that a backup carries. audit.jsonl is kept for the record but never restored.</summary>
    private static readonly string[] StateFiles = ["knowledge-bases.json", "schedules.json", "feedback.jsonl", "usage.jsonl"];
    private const string AuditFile = "audit.jsonl";
    private const long MaxUncompressedBytes = 4L * 1024 * 1024 * 1024;
    private const int MaxEntries = 200_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly RagOptions _options;
    private readonly DocumentRegistry _registry;
    private readonly IVectorStore _store;
    private readonly IngestionQueue _queue;
    private readonly CrawlScheduler _scheduler;
    private readonly KnowledgeBaseStore _knowledgeBases;
    private readonly IEmbeddingClient _embedding;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupService>? _logger;

    public BackupService(IOptions<RagOptions> options, DocumentRegistry registry, IVectorStore store, IngestionQueue queue,
        CrawlScheduler scheduler, KnowledgeBaseStore knowledgeBases, IEmbeddingClient embedding, TimeProvider? time = null,
        ILogger<BackupService>? logger = null)
    {
        _options = options.Value;
        _registry = registry;
        _store = store;
        _queue = queue;
        _scheduler = scheduler;
        _knowledgeBases = knowledgeBases;
        _embedding = embedding;
        _time = time ?? TimeProvider.System;
        _logger = logger;
    }

    private string DocumentsDirectory => Path.Combine(_options.DataDirectory, "documents");

    [GeneratedRegex(@"^documents/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.(txt|vec)$")]
    private static partial Regex DocumentFile();

    /// <summary>Writes the backup to a temporary file (deleted when the returned stream is closed).</summary>
    public async Task<(Stream Zip, string FileName)> CreateAsync(CancellationToken ct)
    {
        var path = Path.Combine(Path.GetTempPath(), $"chatbotrag-backup-{Guid.NewGuid():N}.zip");
        var now = _time.GetUtcNow();

        // Under the registry lock, so no document is half-replaced while it is copied.
        using (await _registry.LockAsync(ct))
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var manifest = new BackupManifest(Format, Version, now, _registry.List().Count, _embedding.ModelId);
            await AddTextAsync(zip, "manifest.json", JsonSerializer.Serialize(manifest, Json), ct);

            // The index from memory, so a backup taken before the first document was stored still has one.
            await AddTextAsync(zip, "documents/index.json", JsonSerializer.Serialize(_registry.List()), ct);
            foreach (var source in Directory.EnumerateFiles(DocumentsDirectory))
            {
                var name = Path.GetFileName(source);
                if (DocumentFile().IsMatch($"documents/{name}"))
                    await AddFileAsync(zip, $"documents/{name}", source, ct);
            }
            foreach (var name in StateFiles.Append(AuditFile))
            {
                var source = Path.Combine(_options.DataDirectory, name);
                if (File.Exists(source)) await AddFileAsync(zip, name, source, ct);
            }
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        return (stream, $"chatbot-backup-{now:yyyyMMdd-HHmmss}.zip");
    }

    /// <summary>
    /// Replaces every document, schedule, rating and usage line with the backup's and queues a re-index of each
    /// document. Refused while ingestion jobs are queued or running. The zip is checked fully (format, version, only
    /// known file names, size) before anything is touched.
    /// </summary>
    /// <returns>The result, or the reason it was refused (Busy = jobs are running; otherwise the file is invalid).</returns>
    public async Task<(RestoreResponse? Result, string? Error, bool Busy)> RestoreAsync(Stream upload, CancellationToken ct)
    {
        if (_queue.HasActiveJobs) return (null, BusyMessage, true);

        var staging = Path.Combine(_options.DataDirectory, "restore-staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(Path.Combine(staging, "documents"));
        try
        {
            BackupManifest manifest;
            List<DocumentRegistry.Entry> entries;
            try
            {
                using var zip = new ZipArchive(upload, ZipArchiveMode.Read);
                if (await ExtractAsync(zip, staging, ct) is { } invalid) return (null, invalid, false);

                manifest = JsonSerializer.Deserialize<BackupManifest>(await File.ReadAllTextAsync(Path.Combine(staging, "manifest.json"), ct), Json)
                    ?? throw new JsonException("empty manifest");
                if (manifest.Format != Format) return (null, "This is not a backup made by this app (manifest.json has another format).", false);
                if (manifest.Version > Version) return (null, $"This backup is version {manifest.Version}; this app reads up to version {Version}. Update the app first.", false);

                entries = JsonSerializer.Deserialize<List<DocumentRegistry.Entry>>(
                    await File.ReadAllTextAsync(Path.Combine(staging, "documents", "index.json"), ct)) ?? [];
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or EndOfStreamException)
            {
                return (null, "The file is not a valid zip.", false);
            }
            catch (JsonException)
            {
                return (null, "The backup's manifest.json or documents/index.json is unreadable.", false);
            }

            using (await _registry.LockAsync(ct))
            {
                if (_queue.HasActiveJobs) return (null, BusyMessage, true);

                // Remove the current documents' points (through DI, so the keyword index forgets them too).
                var current = _registry.List().SelectMany(e => e.ChunkIds).ToList();
                if (current.Count > 0) await _store.DeleteAsync(current, ct);

                foreach (var file in Directory.EnumerateFiles(DocumentsDirectory)) File.Delete(file);
                foreach (var file in Directory.EnumerateFiles(Path.Combine(staging, "documents")))
                    File.Move(file, Path.Combine(DocumentsDirectory, Path.GetFileName(file)));
                _registry.Reload();

                foreach (var name in StateFiles)
                {
                    var target = Path.Combine(_options.DataDirectory, name);
                    var restored = Path.Combine(staging, name);
                    if (File.Exists(restored)) File.Move(restored, target, overwrite: true);
                    else File.Delete(target);
                }
                _scheduler.Reload();
                _knowledgeBases.Reload();
            }

            var jobs = 0;
            foreach (var doc in _registry.List())
            {
                var text = _registry.TextPath(doc.KnowledgeBase, doc.Source);
                if (!File.Exists(text))
                {
                    _logger?.LogWarning("The backup has no text for {Source}; it cannot be re-indexed", doc.Source);
                    continue;
                }
                _queue.Enqueue(IngestionWorker.ReindexSourceType, text, doc.Source, knowledgeBase: doc.KnowledgeBase);
                jobs++;
            }

            _logger?.LogWarning("Restored a backup from {CreatedAt}: {Documents} document(s), {Jobs} re-index job(s) queued",
                manifest.CreatedAt, entries.Count, jobs);
            return (new RestoreResponse(entries.Count, jobs, manifest.CreatedAt), null, false);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private const string BusyMessage = "Ingestion jobs are queued or running; wait for them to finish, then restore.";

    /// <summary>Extracts only known names (so no path can escape the staging folder), within the size limits.</summary>
    private static async Task<string?> ExtractAsync(ZipArchive zip, string staging, CancellationToken ct)
    {
        if (zip.Entries.Count > MaxEntries) return "The backup has too many files.";
        var names = zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("manifest.json") || !names.Contains("documents/index.json"))
            return "The backup must contain manifest.json and documents/index.json.";

        long total = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue; // a folder
            var name = entry.FullName;
            var known = name is "manifest.json" or "documents/index.json" || StateFiles.Contains(name) || name == AuditFile
                || DocumentFile().IsMatch(name);
            if (!known) return $"Unexpected file in the backup: {name}";
            if (name == AuditFile) continue; // the current audit trail is kept, including this restore

            await using var input = entry.Open();
            await using var output = File.Create(Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar)));
            // Count what is actually written: an entry's declared length can lie (a zip bomb).
            var buffer = new byte[81920];
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += read;
                if (total > MaxUncompressedBytes) return "The backup is larger than 4 GB unpacked.";
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
        }
        return null;
    }

    private static async Task AddTextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        await using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open());
        await writer.WriteAsync(text.AsMemory(), ct);
    }

    /// <summary>Shared read: the usage and feedback logs may be appended to while they are copied.</summary>
    private static async Task AddFileAsync(ZipArchive zip, string name, string path, CancellationToken ct)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous);
        await using var output = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        await input.CopyToAsync(output, ct);
    }
}
