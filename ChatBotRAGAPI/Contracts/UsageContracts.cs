namespace ChatBotRAGAPI.Contracts;

/// <summary>One chat question as logged in usage.jsonl.</summary>
/// <param name="Outcome">answered, nodata ("I don't have enough data."), declined (the model refused) or error.</param>
/// <param name="Question">The question as asked; null when Rag:UsageStoreQuestions is off.</param>
public sealed record UsageEntry(DateTimeOffset At, string Outcome, string? Question, int Sources, double Seconds, bool FollowUp);

/// <param name="Date">UTC day, yyyy-MM-dd.</param>
public sealed record UsageDay(string Date, int Questions, int Answered, int NoData, int Declined, int Errors, int Helpful, int NotHelpful);

/// <summary>A question the bot could not answer, with how often it was asked: content that is missing.</summary>
public sealed record UsageQuestion(string Question, int Count, DateTimeOffset LastAskedAt);

/// <param name="AverageSeconds">Mean time to a full answer, answered questions only.</param>
/// <param name="Days">Every day of the period, oldest first, including days without questions.</param>
/// <param name="Unanswered">The most frequent questions answered "I don't have enough data.", up to 10.</param>
public sealed record UsageReport(
    string From, string To, int Questions, int Answered, int NoData, int Declined, int Errors, int Helpful, int NotHelpful,
    double? AverageSeconds, IReadOnlyList<UsageDay> Days, IReadOnlyList<UsageQuestion> Unanswered);
