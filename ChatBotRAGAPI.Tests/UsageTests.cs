using System.Net;
using System.Net.Http.Json;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services;
using ChatBotRAGAPI.Services.Ai;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace ChatBotRAGAPI.Tests;

public sealed class UsageLogTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ragusage-").FullName;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private (UsageLog Usage, FeedbackStore Feedback) Create(bool storeQuestions = true, int maxMb = 50)
    {
        var options = Options.Create(new RagOptions { DataDirectory = _dir, UsageStoreQuestions = storeQuestions, UsageMaxFileMB = maxMb });
        var feedback = new FeedbackStore(options, _time);
        return (new UsageLog(options, feedback, _time), feedback);
    }

    [Theory]
    [InlineData("I don't have enough data.", UsageLog.NoData)]
    [InlineData(ChatClientAnswerGenerator.DeclinedAnswer, UsageLog.Declined)]
    [InlineData("Gold costs 900 a month [1].", UsageLog.Answered)]
    public void Answers_are_classified(string answer, string outcome) => Assert.Equal(outcome, UsageLog.OutcomeOf(answer));

    [Fact]
    public void The_report_has_every_day_totals_ratings_and_answer_time()
    {
        var (usage, feedback) = Create();
        usage.Record("When was it founded?", UsageLog.Answered, 2, TimeSpan.FromSeconds(4), false);
        usage.Record("Pricing?", UsageLog.Answered, 1, TimeSpan.FromSeconds(2), true);
        feedback.Add(new FeedbackRequest("Pricing?", "900", "up"));
        _time.Advance(TimeSpan.FromDays(2));
        usage.Record("Do you sell boats?", UsageLog.NoData, 0, TimeSpan.FromSeconds(1), false);
        usage.Record("x", UsageLog.Error, 0, TimeSpan.FromSeconds(1), false);
        feedback.Add(new FeedbackRequest("x", "y", "down"));

        var report = usage.Report(7);

        Assert.Equal(("2026-11-13", "2026-11-19"), (report.From, report.To));
        Assert.Equal(7, report.Days.Count);
        Assert.Equal((4, 2, 1, 0, 1, 1, 1), (report.Questions, report.Answered, report.NoData, report.Declined, report.Errors, report.Helpful, report.NotHelpful));
        Assert.Equal(3.0, report.AverageSeconds);
        Assert.Equal(new UsageDay("2026-11-17", 2, 2, 0, 0, 0, 1, 0), report.Days.Single(d => d.Date == "2026-11-17"));
        Assert.Equal(new UsageDay("2026-11-19", 2, 0, 1, 0, 1, 0, 1), report.Days[^1]);
        Assert.All(report.Days.Where(d => d.Date is not ("2026-11-17" or "2026-11-19")), d => Assert.Equal(0, d.Questions));
    }

    [Fact]
    public void Older_questions_fall_outside_the_period()
    {
        var (usage, _) = Create();
        usage.Record("old", UsageLog.Answered, 1, TimeSpan.FromSeconds(1), false);
        _time.Advance(TimeSpan.FromDays(10));

        var report = usage.Report(7);

        Assert.Equal(0, report.Questions);
        Assert.Null(report.AverageSeconds);
    }

    [Fact]
    public void Unanswered_questions_are_grouped_and_ranked()
    {
        var (usage, _) = Create();
        foreach (var q in new[] { "Do you sell boats?", "do you sell boats", "Do you sell BOATS?!", "Where is the Paris office?" })
        {
            usage.Record(q, UsageLog.NoData, 0, TimeSpan.FromSeconds(1), false);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
        usage.Record("Answered one", UsageLog.Answered, 1, TimeSpan.FromSeconds(1), false);

        var unanswered = usage.Report(30).Unanswered;

        Assert.Equal(2, unanswered.Count);
        Assert.Equal(("Do you sell BOATS?!", 3), (unanswered[0].Question, unanswered[0].Count));
        Assert.Equal(("Where is the Paris office?", 1), (unanswered[1].Question, unanswered[1].Count));
    }

    [Fact]
    public void Questions_can_be_left_out_of_the_log()
    {
        var (usage, _) = Create(storeQuestions: false);
        usage.Record("Do you sell boats?", UsageLog.NoData, 0, TimeSpan.FromSeconds(1), false);

        Assert.DoesNotContain("boats", File.ReadAllText(Path.Combine(_dir, "usage.jsonl")));
        var report = usage.Report(1);
        Assert.Equal(1, report.NoData);
        Assert.Empty(report.Unanswered);
    }

    [Fact]
    public void Long_questions_are_cut_and_a_full_log_stops_growing()
    {
        var (usage, _) = Create(maxMb: 1);
        var path = Path.Combine(_dir, "usage.jsonl");
        usage.Record(new string('q', 2000), UsageLog.NoData, 0, TimeSpan.FromSeconds(1), false);
        Assert.InRange(new FileInfo(path).Length, 300, 500);

        File.AppendAllText(path, new string(' ', 1024 * 1024));
        var size = new FileInfo(path).Length;
        usage.Record("one more", UsageLog.Answered, 1, TimeSpan.FromSeconds(1), false);
        Assert.Equal(size, new FileInfo(path).Length);
    }
}

public sealed class UsageApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Chat_questions_show_up_in_the_usage_report()
    {
        using var factory = new RagApiFactory();
        using var client = factory.CreateClient();

        await client.PostAsJsonAsync("/api/chat/ask", new AskRequest("Do you sell boats?"), Ct);
        using (var stream = await client.PostAsJsonAsync("/api/chat/ask/stream",
                   new AskRequest("And yachts?", [new ChatMessage("user", "Do you sell boats?"), new ChatMessage("assistant", "No.")]), Ct))
        {
            await stream.Content.ReadAsStringAsync(Ct); // the stream records its outcome when it finishes
        }
        await client.PostAsJsonAsync("/api/chat/ask", new AskRequest(""), Ct); // invalid: not a question, not logged

        var report = await client.GetFromJsonAsync<UsageReport>("/api/usage?days=7", Ct);

        Assert.Equal(2, report!.Questions);
        Assert.Equal(2, report.NoData);
        Assert.Equal(["Do you sell boats?", "And yachts?"], report.Unanswered.Select(q => q.Question).Order().Reverse());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task Days_must_be_in_range(int days)
    {
        using var factory = new RagApiFactory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/usage?days={days}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Usage_requires_the_api_key()
    {
        using var factory = new RagApiFactory(settings: new Dictionary<string, string> { ["Security:ApiKey"] = "k" });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/usage", Ct)).StatusCode);
    }
}
