using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace ChatBotRAGAPI.Tests;

public class ChatActivityTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Idle_returns_at_once()
    {
        var activity = new ChatActivity();
        Assert.False(activity.IsBusy);
        Assert.True(await activity.WaitForIdleAsync(TimeSpan.FromMinutes(1), Ct));
    }

    [Fact]
    public async Task Waits_until_every_chat_request_has_ended()
    {
        var activity = new ChatActivity();
        var first = activity.Begin();
        var second = activity.Begin();

        var wait = activity.WaitForIdleAsync(TimeSpan.FromMinutes(1), Ct);
        first.Dispose();
        first.Dispose(); // disposing twice must not end the other request
        await Task.Delay(50, Ct);
        Assert.False(wait.IsCompleted);

        second.Dispose();
        Assert.True(await wait);
        Assert.False(activity.IsBusy);
    }

    [Fact]
    public async Task The_wait_is_capped_so_ingestion_never_stops()
    {
        var activity = new ChatActivity();
        using var _ = activity.Begin();

        Assert.False(await activity.WaitForIdleAsync(TimeSpan.FromMilliseconds(50), Ct));
        Assert.True(activity.IsBusy);
    }
}

public sealed class ChatPriorityApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly string _dataDir = Directory.CreateTempSubdirectory("ragprio-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private static async Task<Guid> UploadAsync(HttpClient client, string name, string content)
    {
        using var form = new MultipartFormDataContent { { new StringContent(content), "file", name } };
        return (await (await client.PostAsync("/api/ingestion/upload", form, Ct)).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
    }

    private static async Task<string> StatusAsync(HttpClient client, Guid job) =>
        (await client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{job}", Ct))!.Status;

    [Fact]
    public async Task Ingestion_waits_for_a_running_chat_request_before_embedding()
    {
        var embedder = new CountingEmbedder();
        using var app = new RagApiFactory(_dataDir, new Dictionary<string, string> { ["Rag:ChatPriorityMaxWaitSeconds"] = "60" },
            s => s.AddSingleton<IEmbeddingClient>(embedder));
        using var client = app.CreateClient();
        var activity = app.Services.GetRequiredService<ChatActivity>();

        var chat = activity.Begin(); // a question is being answered
        var job = await UploadAsync(client, "handbook.txt", "Refund policy: a full refund within 30 days of purchase.");
        await Task.Delay(400, Ct);
        Assert.Equal(0, embedder.ChunksEmbedded);
        Assert.Equal("Running", await StatusAsync(client, job));

        chat.Dispose(); // the answer is done
        for (var i = 0; i < 200 && await StatusAsync(client, job) != "Completed"; i++) await Task.Delay(25, Ct);
        Assert.Equal("Completed", await StatusAsync(client, job));
        Assert.Equal(1, embedder.ChunksEmbedded);
    }

    [Fact]
    public async Task With_priority_off_ingestion_does_not_wait()
    {
        var embedder = new CountingEmbedder();
        using var app = new RagApiFactory(_dataDir, new Dictionary<string, string> { ["Rag:PrioritizeChat"] = "false" },
            s => s.AddSingleton<IEmbeddingClient>(embedder));
        using var client = app.CreateClient();
        using var chat = app.Services.GetRequiredService<ChatActivity>().Begin();

        var job = await UploadAsync(client, "handbook.txt", "Refund policy: a full refund within 30 days of purchase.");
        for (var i = 0; i < 200 && await StatusAsync(client, job) != "Completed"; i++) await Task.Delay(25, Ct);
        Assert.Equal("Completed", await StatusAsync(client, job));
    }

    /// <summary>Records whether chat was marked busy while the answer was being generated.</summary>
    private sealed class ObservingGenerator(ChatActivity activity) : IAnswerGenerator
    {
        public List<bool> BusyDuringGeneration { get; } = [];

        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct)
        {
            BusyDuringGeneration.Add(activity.IsBusy);
            return Task.FromResult("A full refund within 30 days [1].");
        }

        public async IAsyncEnumerable<string> StreamAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct)
        {
            BusyDuringGeneration.Add(activity.IsBusy);
            await Task.Yield();
            yield return "A full refund ";
            BusyDuringGeneration.Add(activity.IsBusy); // still busy while the stream is being written
            yield return "within 30 days [1].";
        }
    }

    [Fact]
    public async Task Ask_and_stream_mark_chat_busy_until_the_response_ends()
    {
        ObservingGenerator? generator = null;
        using var app = new RagApiFactory(_dataDir, services: s => s.AddSingleton<IAnswerGenerator>(sp =>
            generator = new ObservingGenerator(sp.GetRequiredService<ChatActivity>())));
        using var client = app.CreateClient();
        var activity = app.Services.GetRequiredService<ChatActivity>();

        var job = await UploadAsync(client, "handbook.txt", "Refund policy: a full refund within 30 days of purchase.");
        for (var i = 0; i < 200 && await StatusAsync(client, job) != "Completed"; i++) await Task.Delay(25, Ct);

        await client.PostAsJsonAsync("/api/chat/ask", new AskRequest("What is the refund policy?"), Ct);
        using (var stream = await client.PostAsJsonAsync("/api/chat/ask/stream", new AskRequest("What is the refund policy?"), Ct))
            await stream.Content.ReadAsStringAsync(Ct);

        Assert.Equal([true, true, true], generator!.BusyDuringGeneration);
        for (var i = 0; i < 40 && activity.IsBusy; i++) await Task.Delay(25, Ct); // released as the response completes
        Assert.False(activity.IsBusy);
    }
}
