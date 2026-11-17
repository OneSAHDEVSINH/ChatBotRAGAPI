using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public sealed class BackupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<Guid> UploadAsync(HttpClient client, string fileName, string content)
    {
        using var form = new MultipartFormDataContent { { new StringContent(content), "file", fileName } };
        var response = await client.PostAsync("/api/ingestion/upload", form, Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
    }

    private static async Task WaitForAllJobsAsync(HttpClient client)
    {
        for (var i = 0; i < 200; i++)
        {
            var jobs = await client.GetFromJsonAsync<List<JobInfo>>("/api/ingestion/jobs", Ct);
            if (jobs!.All(j => j.Status is "Completed" or "Failed"))
            {
                Assert.All(jobs!, j => Assert.Equal("Completed", j.Status));
                return;
            }
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("Jobs did not finish.");
    }

    private static async Task<HttpResponseMessage> RestoreAsync(HttpClient client, byte[] zip)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(zip);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(file, "file", "backup.zip");
        return await client.PostAsync("/api/backup/restore", form, Ct);
    }

    private static async Task<string> AskAsync(HttpClient client, string question) =>
        (await (await client.PostAsJsonAsync("/api/chat/ask", new AskRequest(question), Ct)).Content.ReadFromJsonAsync<AskResponse>(Ct))!.Answer;

    [Fact]
    public async Task A_backup_restores_documents_schedules_and_ratings_on_another_server()
    {
        byte[] backup;
        using (var source = new RagApiFactory())
        using (var client = source.CreateClient())
        {
            await UploadAsync(client, "warranty.txt", "Warranty: all devices carry a two year limited warranty.");
            await WaitForAllJobsAsync(client);
            await client.PostAsJsonAsync("/api/schedules", new CrawlScheduleRequest("https://example.com/", 5, true, 24), Ct);
            await client.PostAsJsonAsync("/api/feedback", new FeedbackRequest("Warranty?", "Two years.", "up"), Ct);

            var response = await client.GetAsync("/api/backup", Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            Assert.StartsWith("chatbot-backup-", response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
            backup = await response.Content.ReadAsByteArrayAsync(Ct);
        }

        using (var zip = new ZipArchive(new MemoryStream(backup)))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("manifest.json", names);
            Assert.Contains("documents/index.json", names);
            Assert.Contains(names, n => n.StartsWith("documents/") && n.EndsWith(".txt"));
            Assert.Contains(names, n => n.StartsWith("documents/") && n.EndsWith(".vec"));
            Assert.Contains("schedules.json", names);
            Assert.Contains("feedback.jsonl", names);
        }

        using var target = new RagApiFactory();
        using var restored = target.CreateClient();
        await UploadAsync(restored, "other.txt", "Shipping: parcels leave the warehouse within one day.");
        await WaitForAllJobsAsync(restored);

        var result = await RestoreAsync(restored, backup);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var body = (await result.Content.ReadFromJsonAsync<RestoreResponse>(Ct))!;
        Assert.Equal((1, 1), (body.Documents, body.ReindexJobs));
        await WaitForAllJobsAsync(restored);

        var documents = await restored.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct);
        Assert.Equal(["warranty.txt"], documents!.Select(d => d.Source));          // replaced, not merged
        Assert.Contains("two year", await AskAsync(restored, "How long is the warranty?"));
        Assert.Equal(RagService.NoDataAnswer, await AskAsync(restored, "When do parcels leave the warehouse?")); // gone from search too
        Assert.Single((await restored.GetFromJsonAsync<List<CrawlScheduleInfo>>("/api/schedules", Ct))!);
        Assert.Equal(1, (await restored.GetFromJsonAsync<FeedbackList>("/api/feedback", Ct))!.Up);
    }

    private static byte[] Zip(params (string Name, string Content)[] files)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private const string Manifest = """{"format":"chatbotrag-backup","version":1,"createdAt":"2026-11-17T00:00:00Z","documents":0,"embeddingModel":"x"}""";

    public static TheoryData<byte[], string> InvalidBackups => new()
    {
        { Encoding.UTF8.GetBytes("not a zip"), "not a valid zip" },
        { Zip(("documents/index.json", "[]")), "must contain manifest.json" },
        { Zip(("manifest.json", Manifest), ("documents/index.json", "[]"), ("../../evil.txt", "x")), "Unexpected file" },
        { Zip(("manifest.json", Manifest), ("documents/index.json", "[]"), ("documents/notes.txt", "x")), "Unexpected file" },
        { Zip(("manifest.json", Manifest.Replace("chatbotrag-backup", "other")), ("documents/index.json", "[]")), "another format" },
        { Zip(("manifest.json", Manifest.Replace("\"version\":1", "\"version\":9")), ("documents/index.json", "[]")), "version 9" },
        { Zip(("manifest.json", Manifest), ("documents/index.json", "{oops")), "unreadable" },
    };

    [Theory]
    [MemberData(nameof(InvalidBackups))]
    public async Task Invalid_backups_are_refused_and_change_nothing(byte[] zip, string error)
    {
        using var factory = new RagApiFactory();
        using var client = factory.CreateClient();
        await UploadAsync(client, "warranty.txt", "Warranty: all devices carry a two year limited warranty.");
        await WaitForAllJobsAsync(client);

        var response = await RestoreAsync(client, zip);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(Ct));
        Assert.Single((await client.GetFromJsonAsync<List<DocumentInfo>>("/api/documents", Ct))!);
        Assert.False(File.Exists(Path.Combine(factory.DataDirectory, "..", "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(factory.DataDirectory, "restore-staging")));
    }

    /// <summary>Holds every embedding call until released, so a job stays Running.</summary>
    private sealed class GatedEmbedder(IOptions<RagOptions> options) : IEmbeddingClient
    {
        private readonly LocalEmbeddingClient _inner = new(options);
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string ModelId => _inner.ModelId;
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => _inner.EmbedAsync(text, ct);
        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
        {
            await Gate.Task.WaitAsync(ct);
            return await _inner.EmbedBatchAsync(texts, ct);
        }
    }

    [Fact]
    public async Task Restore_waits_for_running_jobs()
    {
        using var factory = new RagApiFactory(services: s => s.AddSingleton<IEmbeddingClient, GatedEmbedder>());
        using var client = factory.CreateClient();
        var gate = (GatedEmbedder)factory.Services.GetRequiredService<IEmbeddingClient>();

        await UploadAsync(client, "warranty.txt", "Warranty: all devices carry a two year limited warranty.");
        var backup = await (await client.GetAsync("/api/backup", Ct)).Content.ReadAsByteArrayAsync(Ct);

        var response = await RestoreAsync(client, backup);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        gate.Gate.SetResult();
        await WaitForAllJobsAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await RestoreAsync(client, backup)).StatusCode);
    }

    [Fact]
    public async Task Backups_require_the_api_key()
    {
        using var factory = new RagApiFactory(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "k" });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/backup", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RestoreAsync(client, [1])).StatusCode);
    }
}

public sealed class AuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Management_changes_and_rejected_keys_are_audited_but_reads_are_not()
    {
        using var factory = new RagApiFactory(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "k" });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "k");
        using var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Add("X-Api-Key", "guess");

        using (var form = new MultipartFormDataContent { { new StringContent("Warranty: two years."), "file", "warranty.txt" } })
            await client.PostAsync("/api/ingestion/upload", form, Ct);
        await client.DeleteAsync("/api/documents?source=missing.txt", Ct);
        var schedule = await (await client.PostAsJsonAsync("/api/schedules", new CrawlScheduleRequest("https://example.com/", 5, true, 24), Ct))
            .Content.ReadFromJsonAsync<CrawlScheduleInfo>(Ct);
        await client.DeleteAsync($"/api/schedules/{schedule!.Id}", Ct);
        await client.GetAsync("/api/documents", Ct);            // a read: not audited
        await client.GetAsync("/api/backup", Ct);               // a read of everything: audited
        await stranger.DeleteAsync("/api/documents?source=warranty.txt", Ct);

        var entries = (await client.GetFromJsonAsync<List<AuditEntry>>("/api/audit", Ct))!;

        Assert.Equal(
        [
            ("auth.rejected", "DELETE /api/documents (wrong key)", 401),
            ("admindata.backup", entries[1].Target, 200),
            ("schedules.delete", schedule.Id.ToString(), 204),
            ("schedules.create", "https://example.com/", 201),
            ("documents.delete", "missing.txt", 404),
            ("ingestion.upload", "warranty.txt", 202),
        ], entries.Select(e => (e.Action, e.Target, e.Status)));
        Assert.StartsWith("chatbot-backup-", entries[1].Target);
        Assert.All(entries.Skip(1), e => Assert.Equal("api key", e.Actor));
    }

    [Fact]
    public void A_full_log_rolls_over_and_both_files_are_listed()
    {
        var dir = Directory.CreateTempSubdirectory("ragaudit-").FullName;
        try
        {
            var options = Options.Create(new RagOptions { DataDirectory = dir, AuditMaxFileMB = 1 });
            var audit = new AuditLog(options);
            var http = new DefaultHttpContext
            {
                RequestServices = new ServiceCollection().AddSingleton(Options.Create(new SecurityOptions())).BuildServiceProvider(),
            };
            audit.Record(http, "first", null, 200);
            File.AppendAllText(Path.Combine(dir, "audit.jsonl"), new string(' ', 1024 * 1024) + "\n");

            audit.Record(http, "second", null, 200);

            Assert.True(File.Exists(Path.Combine(dir, "audit.1.jsonl")));
            Assert.Equal(["second", "first"], audit.List(10).Select(e => e.Action));
            Assert.Equal("no key configured", audit.List(1)[0].Actor);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
