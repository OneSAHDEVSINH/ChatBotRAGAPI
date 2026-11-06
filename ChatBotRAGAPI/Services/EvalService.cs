using System.Diagnostics;
using System.Text.RegularExpressions;
using ChatBotRAGAPI.Contracts;
using ChatBotRAGAPI.Services.Ai;
using ChatBotRAGAPI.Settings;
using Microsoft.Extensions.Options;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Measures the bot against a list of known questions. <see cref="CalibrateAsync"/> looks only at raw vector scores,
/// to place the relevance threshold for the current embedding model; <see cref="EvaluateAnswersAsync"/> asks each
/// question for real and checks retrieval, facts, citations and refusals, so a change of model or prompt is a number.
/// </summary>
public sealed partial class EvalService
{
    private readonly IEmbeddingClient _embeddings;
    private readonly IVectorStore _store;
    private readonly RagService _rag;
    private readonly RagOptions _options;

    public EvalService(IEmbeddingClient embeddings, IVectorStore store, RagService rag, IOptions<RagOptions> options)
    {
        _embeddings = embeddings;
        _store = store;
        _rag = rag;
        _options = options.Value;
    }

    public async Task<CalibrationReport> CalibrateAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct)
    {
        var current = _options.EffectiveMinScore;
        var setting = _options.UseLocalAiFallback ? "Rag:LocalMinScore" : "Rag:MinScore";
        var pool = Math.Max(_options.TopK, _options.SearchCandidates);

        var results = new List<CalibrationCaseResult>();
        foreach (var c in cases)
        {
            var hits = await _store.SearchAsync(await _embeddings.EmbedAsync(c.Question, ct), pool, ct);
            var best = c.ExpectedSource is null
                ? hits.FirstOrDefault()
                : hits.Where(h => h.Source == c.ExpectedSource).MaxBy(h => h.Score);
            var score = best is null ? (double?)null : Math.Round(best.Score, 4);
            var passes = c.ExpectedSource is null ? (score ?? 0) < current : score >= current;
            results.Add(new CalibrationCaseResult(c.Question, c.ExpectedSource, score, best?.Source, passes));
        }

        var relevant = results.Where(r => r.ExpectedSource is not null).ToList();
        var offTopic = results.Where(r => r.ExpectedSource is null).ToList();
        var lowest = relevant.Count == 0 || relevant.Any(r => r.BestScore is null) ? null : relevant.Min(r => r.BestScore);
        double? highest = offTopic.Count == 0 ? null : offTopic.Max(r => r.BestScore ?? 0);
        var separable = lowest is not null && highest is not null && lowest > highest;
        double? suggested = separable ? Math.Round((lowest!.Value + highest!.Value) / 2, 3) : null;

        var note = (relevant.Count, offTopic.Count) switch
        {
            (0, _) or (_, 0) => "Add both answerable and off-topic questions: the threshold sits between the two groups.",
            _ when relevant.Any(r => r.BestScore is null) =>
                "Some expected sources are not among the top candidates at all; check they are ingested and named exactly as listed.",
            _ when separable && lowest - highest < 0.05 =>
                $"Separable, but only by {lowest - highest:0.000}: add more questions before trusting the suggestion.",
            _ when separable => $"Set {setting} to {suggested} (midway between the weakest relevant and strongest off-topic score).",
            _ => "Relevant and off-topic scores overlap, so no threshold separates them all; hybrid keyword search can still admit exact-term questions.",
        };

        return new CalibrationReport(_embeddings.ModelId, setting, current, suggested, separable, lowest, highest,
            results.Count(r => r.PassesAtCurrent), results.Count, note, results);
    }

    public async Task<AnswerEvalReport> EvaluateAnswersAsync(IReadOnlyList<EvalCase> cases, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var results = new List<AnswerCaseResult>();
        foreach (var c in cases)
        {
            var watch = Stopwatch.StartNew();
            var response = await _rag.AskAsync(c.Question, null, ct);
            var sources = response.Sources.Select(s => s.Source).ToList();
            var answer = response.Answer.Trim();
            var refused = answer == RagService.NoDataAnswer || answer == ChatClientAnswerGenerator.DeclinedAnswer;

            var cited = Citation().Matches(answer).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
            var invalid = cited.Where(n => n < 1 || n > sources.Count).ToList();
            var citesExpected = c.ExpectedSource is not null && cited.Any(n => n >= 1 && n <= sources.Count && sources[n - 1] == c.ExpectedSource);
            var missing = (c.MustContain ?? []).Where(f => !answer.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
            var retrieved = c.ExpectedSource is not null && sources.Contains(c.ExpectedSource);

            var passed = c.ExpectedSource is null
                ? refused
                : retrieved && !refused && missing.Count == 0 && citesExpected && invalid.Count == 0;

            results.Add(new AnswerCaseResult(c.Question, c.ExpectedSource, passed, answer, sources, retrieved, refused,
                missing, citesExpected, invalid, (int)watch.ElapsedMilliseconds));
        }

        var answerable = results.Where(r => r.ExpectedSource is not null).ToList();
        var offTopic = results.Where(r => r.ExpectedSource is null).ToList();
        var chatModel = _options.UseLocalAiFallback ? "local fallback" : $"{_options.ChatProvider.ToLowerInvariant()}:{_options.ChatModel}";

        return new AnswerEvalReport(chatModel, _embeddings.ModelId, results.Count, results.Count(r => r.Passed),
            answerable.Count, answerable.Count(r => r.Retrieved),
            answerable.Count(r => !r.Refused && r.MissingFacts.Count == 0),
            answerable.Count(r => r.CitesExpected && r.InvalidCitations.Count == 0),
            offTopic.Count, offTopic.Count(r => r.Refused), (int)total.ElapsedMilliseconds, results);
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex Citation();
}
