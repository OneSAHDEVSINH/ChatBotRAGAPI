using ChatBotRAGAPI.Contracts;

namespace ChatBotRAGAPI.Services;

/// <summary>No-LLM fallback: returns the retrieved passages that share terms with the question, labelled with their [n] citation.</summary>
public sealed class LocalAnswerGenerator : IAnswerGenerator
{
    private const string NoData = "I don't have enough data.";

    public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<SearchHit> context, CancellationToken ct)
    {
        var qTokens = Tokenize(question);
        var ranked = context
            .Select((hit, i) => new { Id = i + 1, Text = hit.Text.Trim(), Score = OverlapScore(qTokens, Tokenize(hit.Text)) })
            .Where(x => x.Score > 0 && x.Text.Length > 0)
            .OrderByDescending(x => x.Score)
            .Take(3)
            .ToList();

        if (ranked.Count == 0)
            return Task.FromResult(NoData);

        var answer = string.Join(Environment.NewLine + Environment.NewLine, ranked.Select(x => $"[{x.Id}] {x.Text}"));
        if (answer.Length > 1500) answer = answer[..1500];

        return Task.FromResult(answer);
    }

    private static HashSet<string> Tokenize(string text) => LocalText.Tokenize(text).ToHashSet();

    private static int OverlapScore(HashSet<string> a, HashSet<string> b) => a.Count(b.Contains);
}
