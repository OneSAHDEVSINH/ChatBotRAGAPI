using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class VectorFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vecfile-").FullName;
    private string Path_ => Path.Combine(_dir, "doc.vec");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Round_trips_vectors_by_id()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        VectorFile.Write(Path_, "model:3", new Dictionary<Guid, float[]> { [a] = [1f, -2.5f, 3f], [b] = [0f, 0.125f, float.Epsilon] });

        var read = VectorFile.Read(Path_, "model:3");

        Assert.Equal(2, read.Count);
        Assert.Equal([1f, -2.5f, 3f], read[a]);
        Assert.Equal([0f, 0.125f, float.Epsilon], read[b]);
        Assert.False(File.Exists(Path_ + ".tmp"));
    }

    [Fact]
    public void Another_model_reads_as_empty()
    {
        VectorFile.Write(Path_, "ollama:nomic:768", new Dictionary<Guid, float[]> { [Guid.NewGuid()] = [1f] });
        Assert.Empty(VectorFile.Read(Path_, "ollama:mxbai:1024"));
    }

    [Fact]
    public void Missing_truncated_or_foreign_files_read_as_empty()
    {
        Assert.Empty(VectorFile.Read(Path_, "m"));

        VectorFile.Write(Path_, "m", new Dictionary<Guid, float[]> { [Guid.NewGuid()] = [1f, 2f, 3f, 4f] });
        var bytes = File.ReadAllBytes(Path_);
        File.WriteAllBytes(Path_, bytes[..^5]); // cut off mid-vector
        Assert.Empty(VectorFile.Read(Path_, "m"));

        File.WriteAllText(Path_, "not a vector file at all");
        Assert.Empty(VectorFile.Read(Path_, "m"));
    }

    [Fact]
    public void Vectors_of_different_lengths_are_refused() =>
        Assert.Throws<InvalidOperationException>(() => VectorFile.Write(Path_, "m",
            new Dictionary<Guid, float[]> { [Guid.NewGuid()] = [1f, 2f], [Guid.NewGuid()] = [1f] }));
}

/// <summary>The local fallback embedder, counting the chunk texts ingestion asks it to embed.</summary>
internal sealed class CountingEmbedder(string? modelId = null) : IEmbeddingClient
{
    private readonly LocalEmbeddingClient _inner = new(Options.Create(new RagOptions()));
    private int _chunks;

    public int ChunksEmbedded => Volatile.Read(ref _chunks);
    public string ModelId => modelId ?? _inner.ModelId;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) => _inner.EmbedAsync(text, ct); // questions

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        Interlocked.Add(ref _chunks, texts.Count);
        return _inner.EmbedBatchAsync(texts, ct);
    }
}

public sealed class EmbeddingCacheTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ragcache-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    // Three paragraphs, each its own chunk at the default ChunkSize (700).
    private static readonly string[] Paragraphs =
    [
        "Refund policy: customers may request a full refund within 30 days of purchase. " + new string('r', 400),
        "Warranty: every device carries a two year limited warranty covering parts and labour. " + new string('w', 400),
        "Support desk: open Monday to Friday from nine to five, closed on public holidays. " + new string('s', 400),
    ];

    private RagApiFactory App(CountingEmbedder embedder) =>
        new(_dataDir, services: s => s.AddSingleton<IEmbeddingClient>(embedder));

    private static async Task UploadAndWaitAsync(HttpClient client, string name, string content)
    {
        using var form = new MultipartFormDataContent { { new StringContent(content), "file", name } };
        var job = (await (await client.PostAsync("/api/ingestion/upload", form, Ct)).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!;
        for (var i = 0; i < 200; i++)
        {
            var status = await client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{job.JobId}", Ct);
            if (status!.Status == "Completed") return;
            Assert.NotEqual("Failed", status.Status);
            await Task.Delay(25, Ct);
        }
        throw new TimeoutException("Ingestion did not finish.");
    }

    /// <summary>After a restart the in-memory store is rebuilt by background jobs; wait until a question finds the document.</summary>
    private static async Task<AskResponse> AskWhenIndexedAsync(HttpClient client, string question)
    {
        AskResponse? result = null;
        for (var i = 0; i < 200 && (result is null || result.Sources.Count == 0); i++)
        {
            if (i > 0) await Task.Delay(25, Ct);
            result = await (await client.PostAsJsonAsync("/api/chat/ask", new AskRequest(question), Ct)).Content.ReadFromJsonAsync<AskResponse>(Ct);
        }
        return result!;
    }

    [Fact]
    public async Task A_restart_rebuilds_the_store_without_embedding_anything()
    {
        var first = new CountingEmbedder();
        using (var app = App(first))
        using (var client = app.CreateClient())
            await UploadAndWaitAsync(client, "policies.txt", string.Join("\n\n", Paragraphs));
        Assert.Equal(3, first.ChunksEmbedded);

        var second = new CountingEmbedder();
        using var restarted = App(second);
        using var c2 = restarted.CreateClient();
        var answer = await AskWhenIndexedAsync(c2, "What is the warranty?");

        Assert.Equal("policies.txt", answer.Sources[0].Source);
        Assert.Equal(0, second.ChunksEmbedded); // every vector came from data/documents/*.vec
    }

    [Fact]
    public async Task Reingesting_embeds_only_the_chunks_that_changed()
    {
        var embedder = new CountingEmbedder();
        using var app = App(embedder);
        using var client = app.CreateClient();

        await UploadAndWaitAsync(client, "policies.txt", string.Join("\n\n", Paragraphs));
        await UploadAndWaitAsync(client, "policies.txt", string.Join("\n\n", Paragraphs)); // identical: nothing to embed
        Assert.Equal(3, embedder.ChunksEmbedded);

        var edited = Paragraphs.ToArray();
        edited[1] = edited[1].Replace("two year", "five year");
        await UploadAndWaitAsync(client, "policies.txt", string.Join("\n\n", edited));
        Assert.Equal(4, embedder.ChunksEmbedded); // only the warranty paragraph

        var answer = await AskWhenIndexedAsync(client, "How long is the warranty?");
        Assert.Contains("five year", string.Concat(answer.Context));
        Assert.DoesNotContain(answer.Context, c => c.Contains("two year"));
    }

    [Fact]
    public async Task A_different_embedding_model_re_embeds_everything()
    {
        using (var app = App(new CountingEmbedder("model-a")))
        using (var client = app.CreateClient())
            await UploadAndWaitAsync(client, "policies.txt", string.Join("\n\n", Paragraphs));

        var other = new CountingEmbedder("model-b");
        using var restarted = App(other);
        using var c2 = restarted.CreateClient();
        await AskWhenIndexedAsync(c2, "What is the warranty?");

        Assert.Equal(3, other.ChunksEmbedded);
    }

    [Fact]
    public async Task Deleting_a_document_deletes_its_cached_vectors()
    {
        using var app = App(new CountingEmbedder());
        using var client = app.CreateClient();
        await UploadAndWaitAsync(client, "policies.txt", string.Join("\n\n", Paragraphs));

        var vec = Assert.Single(Directory.GetFiles(Path.Combine(_dataDir, "documents"), "*.vec"));
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/documents?source=policies.txt", Ct)).StatusCode);
        Assert.False(File.Exists(vec));
    }
}
