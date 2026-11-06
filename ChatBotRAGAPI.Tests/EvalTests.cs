using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Tests;

public class EvalServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Each question embeds to a fixed vector, so cosine scores against the stored points are known.</summary>
    private sealed class TableEmbedder(Dictionary<string, float[]> vectors) : IEmbeddingClient
    {
        public string ModelId => "table:2";
        public Task<float[]> EmbedAsync(string text, CancellationToken ct) => Task.FromResult(vectors[text]);
        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<float[]>>(texts.Select(t => vectors[t]).ToList());
    }

    private sealed class ScriptedAnswers(Func<IReadOnlyList<SearchHit>, string> answer) : IAnswerGenerator
    {
        public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, CancellationToken ct) =>
            Task.FromResult(answer(context));

        public async IAsyncEnumerable<string> StreamAnswerAsync(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield return answer(context);
        }
    }

    // Unit vectors at known angles: cos(a, b) is the cosine of the angle between them.
    private static float[] At(double degrees) => [(float)Math.Cos(degrees * Math.PI / 180), (float)Math.Sin(degrees * Math.PI / 180)];

    private static async Task<EvalService> ServiceAsync(Dictionary<string, float[]> questions, Func<IReadOnlyList<SearchHit>, string>? answer = null, double minScore = 0.5)
    {
        var store = new InMemoryVectorStore();
        await store.UpsertAsync(
        [
            new VectorPoint(Guid.NewGuid(), At(0), "Founded: 2016. Head office: Ahmedabad.", "profile.txt"),
            new VectorPoint(Guid.NewGuid(), At(90), "Pro plan: 999 per month.", "catalog.csv"),
        ], Ct);
        var options = Options.Create(new RagOptions { MinScore = minScore, TopK = 2, HybridSearch = false });
        var embedder = new TableEmbedder(questions);
        var rag = new RagService(embedder, new ScriptedAnswers(answer ?? (_ => "ok")), store, options);
        return new EvalService(embedder, store, rag, options);
    }

    [Fact]
    public async Task Calibration_suggests_the_midpoint_when_relevant_and_off_topic_separate()
    {
        var eval = await ServiceAsync(new()
        {
            ["When was it founded?"] = At(10),     // cos 0.985 to profile.txt
            ["How much is Pro?"] = At(70),         // cos 0.940 to catalog.csv
            ["Bake bread?"] = At(225),             // best cos is negative-ish: off topic
        });

        var report = await eval.CalibrateAsync(
        [
            new("When was it founded?", "profile.txt"),
            new("How much is Pro?", "catalog.csv"),
            new("Bake bread?"),
        ], Ct);

        Assert.True(report.Separable);
        Assert.Equal(0.9397, report.LowestRelevant!.Value, 3);
        Assert.Equal(Math.Round((report.LowestRelevant.Value + report.HighestOffTopic!.Value) / 2, 3), report.Suggested);
        Assert.Equal(("Rag:MinScore", 0.5, "table:2"), (report.Setting, report.Current, report.EmbeddingModel));
        Assert.Equal(3, report.PassingAtCurrent);
        Assert.Contains("Set Rag:MinScore to", report.Note);
    }

    [Fact]
    public async Task Calibration_reports_overlap_without_a_suggestion()
    {
        var eval = await ServiceAsync(new()
        {
            ["Vague company question"] = At(60),   // cos 0.5 to profile.txt
            ["Off topic but close"] = At(30),      // cos 0.87 to profile.txt
        });

        var report = await eval.CalibrateAsync([new("Vague company question", "profile.txt"), new("Off topic but close")], Ct);

        Assert.False(report.Separable);
        Assert.Null(report.Suggested);
        Assert.Contains("overlap", report.Note);
        Assert.False(report.Cases[1].PassesAtCurrent); // the off-topic question would be admitted at 0.5
    }

    [Fact]
    public async Task An_answer_passes_only_with_its_facts_a_valid_citation_of_the_right_source()
    {
        var eval = await ServiceAsync(new() { ["When was it founded?"] = At(5) },
            answer: hits => $"Founded in 2016 [1]. See also [7].", minScore: 0.5);

        var report = await eval.EvaluateAnswersAsync([new("When was it founded?", "profile.txt", ["2016", "Ahmedabad"])], Ct);

        var result = Assert.Single(report.Cases);
        Assert.False(result.Passed);
        Assert.True(result.Retrieved);
        Assert.True(result.CitesExpected);
        Assert.Equal(["Ahmedabad"], result.MissingFacts);
        Assert.Equal([7], result.InvalidCitations);
        Assert.Equal((1, 1, 0, 0), (report.Answerable, report.Retrieved, report.Correct, report.CitedCorrectly));
    }

    [Fact]
    public async Task A_complete_cited_answer_and_a_refused_off_topic_question_both_pass()
    {
        var eval = await ServiceAsync(new() { ["When was it founded?"] = At(5), ["Bake bread?"] = At(225) },
            answer: _ => "Founded in 2016, head office in Ahmedabad [1].");

        var report = await eval.EvaluateAnswersAsync(
            [new("When was it founded?", "profile.txt", ["2016", "ahmedabad"]), new("Bake bread?")], Ct);

        Assert.Equal(2, report.Passed);
        Assert.Equal((1, 1), (report.OffTopic, report.RefusedCorrectly));
        Assert.True(report.Cases[1].Refused);
    }
}

public sealed class EvalApiTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string TestData = Path.Combine(AppContext.BaseDirectory, "testdata");
    private readonly RagApiFactory _factory = new();
    private readonly HttpClient _client;

    public EvalApiTests() => _client = _factory.CreateClient();

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static EvalRequest SampleEval() =>
        JsonSerializer.Deserialize<EvalRequest>(File.ReadAllText(Path.Combine(TestData, "eval.json")), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void The_sample_eval_names_only_files_that_exist()
    {
        var eval = SampleEval();
        Assert.True(eval.Cases.Count >= 10);
        Assert.All(eval.Cases.Where(c => c.ExpectedSource is not null), c => Assert.True(File.Exists(Path.Combine(TestData, c.ExpectedSource!)), c.ExpectedSource));
        Assert.Contains(eval.Cases, c => c.ExpectedSource is null);
    }

    private async Task IngestSamplesAsync()
    {
        foreach (var file in new[] { "company_profile.txt", "service_catalog.csv", "client_success_story.docx", "quarterly_kpi.xlsx" })
        {
            using var form = new MultipartFormDataContent { { new ByteArrayContent(File.ReadAllBytes(Path.Combine(TestData, file))), "file", file } };
            var job = (await (await _client.PostAsync("/api/ingestion/upload", form, Ct)).Content.ReadFromJsonAsync<EnqueueResponse>(Ct))!.JobId;
            for (var i = 0; i < 200 && (await _client.GetFromJsonAsync<JobStatusResponse>($"/api/ingestion/{job}", Ct))!.Status != "Completed"; i++)
                await Task.Delay(25, Ct);
        }
    }

    [Fact]
    public async Task The_sample_eval_runs_end_to_end_on_the_sample_files()
    {
        await IngestSamplesAsync();

        var calibration = await (await _client.PostAsJsonAsync("/api/eval/calibrate", SampleEval(), Ct)).Content.ReadFromJsonAsync<CalibrationReport>(Ct);
        Assert.Equal("Rag:LocalMinScore", calibration!.Setting);
        Assert.Equal(SampleEval().Cases.Count, calibration.Total);

        var answers = await (await _client.PostAsJsonAsync("/api/eval/answers", SampleEval(), Ct)).Content.ReadFromJsonAsync<AnswerEvalReport>(Ct);
        Assert.Equal("local fallback", answers!.ChatModel);
        Assert.Equal(answers.OffTopic, answers.RefusedCorrectly);       // off-topic questions are refused
        Assert.True(answers.Retrieved >= answers.Answerable - 1, $"retrieved {answers.Retrieved} of {answers.Answerable}");
        Assert.Contains(answers.Cases, c => c.Question == "When was the company founded?" && c.Passed);
    }

    [Theory]
    [InlineData("""{ "cases": [] }""")]
    [InlineData("""{ "cases": [ { "question": " " } ] }""")]
    public async Task Bad_requests_are_rejected(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/eval/calibrate", content, Ct)).StatusCode);
    }

    [Fact]
    public async Task Eval_endpoints_require_the_api_key()
    {
        using var secured = new RagApiFactory(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "k" });
        using var client = secured.CreateClient();
        var response = await client.PostAsJsonAsync("/api/eval/answers", new EvalRequest([new("q")]), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
