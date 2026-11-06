namespace ChatBotRAGAPI.Contracts;

/// <param name="Question">Asked exactly as a user would.</param>
/// <param name="ExpectedSource">The document that answers it (file name or URL); null when the bot should refuse.</param>
/// <param name="MustContain">Facts the answer must include, matched case-insensitively (answers eval only).</param>
public sealed record EvalCase(string Question, string? ExpectedSource = null, IReadOnlyList<string>? MustContain = null);

/// <summary>The same case list drives both <c>/api/eval/calibrate</c> and <c>/api/eval/answers</c> (see testdata/eval.json).</summary>
public sealed record EvalRequest(IReadOnlyList<EvalCase> Cases);

/// <param name="BestScore">For an answerable case, the best cosine of a passage from the expected source; for an
/// off-topic case, the best cosine of any passage. Null when the expected source is not in the top candidates.</param>
/// <param name="PassesAtCurrent">Answerable: BestScore reaches the current threshold. Off-topic: nothing does.</param>
public sealed record CalibrationCaseResult(string Question, string? ExpectedSource, double? BestScore, string? BestSource, bool PassesAtCurrent);

/// <param name="Setting">The threshold that applies: Rag:MinScore, or Rag:LocalMinScore with the local fallback.</param>
/// <param name="LowestRelevant">The weakest score among answerable cases (the threshold must stay below it).</param>
/// <param name="HighestOffTopic">The strongest score among off-topic cases (the threshold must stay above it).</param>
/// <param name="Suggested">Midway between the two when they separate cleanly, else null.</param>
public sealed record CalibrationReport(
    string EmbeddingModel, string Setting, double Current, double? Suggested, bool Separable,
    double? LowestRelevant, double? HighestOffTopic, int PassingAtCurrent, int Total, string Note,
    IReadOnlyList<CalibrationCaseResult> Cases);

/// <param name="Retrieved">The expected source is among the passages given to the model.</param>
/// <param name="Refused">The answer was "I don't have enough data." (or the provider declined).</param>
/// <param name="MissingFacts">MustContain entries the answer lacks.</param>
/// <param name="CitesExpected">At least one [n] in the answer points to a passage from the expected source.</param>
/// <param name="InvalidCitations">[n] markers that point past the passages that were given.</param>
public sealed record AnswerCaseResult(
    string Question, string? ExpectedSource, bool Passed, string Answer, IReadOnlyList<string> Sources,
    bool Retrieved, bool Refused, IReadOnlyList<string> MissingFacts, bool CitesExpected, IReadOnlyList<int> InvalidCitations,
    int DurationMs);

/// <param name="Answerable">Cases with an expected source.</param>
/// <param name="Correct">Answerable cases answered with every expected fact.</param>
/// <param name="CitedCorrectly">Answerable cases citing their expected source with no invalid citation.</param>
/// <param name="RefusedCorrectly">Off-topic cases answered "I don't have enough data.".</param>
public sealed record AnswerEvalReport(
    string ChatModel, string EmbeddingModel, int Total, int Passed, int Answerable, int Retrieved, int Correct,
    int CitedCorrectly, int OffTopic, int RefusedCorrectly, int DurationMs, IReadOnlyList<AnswerCaseResult> Cases);
