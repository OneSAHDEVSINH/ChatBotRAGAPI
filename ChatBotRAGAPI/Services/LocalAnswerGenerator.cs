using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

public sealed class LocalAnswerGenerator : IAnswerGenerator
{
    private static readonly Regex TokenRegex = new("[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public Task<string> GenerateAnswerAsync(string question, IReadOnlyList<string> context, CancellationToken ct)
    {
        if (context.Count == 0)
            return Task.FromResult("I don't have enough data.");

        var qTokens = Tokenize(question);
        var ranked = context
            .Select(c => new { Text = c, Score = OverlapScore(qTokens, Tokenize(c)) })
            .OrderByDescending(x => x.Score)
            .Take(3)
            .Select(x => x.Text)
            .ToList();

        if (ranked.Count == 0 || ranked.All(string.IsNullOrWhiteSpace))
            return Task.FromResult("I don't have enough data.");

        var answer = string.Join(Environment.NewLine + Environment.NewLine, ranked);
        if (answer.Length > 1500) answer = answer[..1500];

        return Task.FromResult(answer);
    }

    private static HashSet<string> Tokenize(string text) =>
        TokenRegex.Matches(text.ToLowerInvariant()).Select(m => m.Value).ToHashSet();

    private static int OverlapScore(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var score = 0;
        foreach (var t in a)
        {
            if (b.Contains(t)) score++;
        }

        return score;
    }
}