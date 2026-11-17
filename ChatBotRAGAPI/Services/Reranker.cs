using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Reorders retrieved passages by how much each helps answer the question, using the chat model (listwise: one call
/// sees every candidate). Retrieval scores say "similar"; this asks "useful", which matters when several documents
/// are close (a price list and a brochure that both mention the plan).
/// </summary>
public interface IReranker
{
    /// <param name="passages">Candidate texts, in retrieval order.</param>
    /// <returns>0-based indexes of the useful passages, most useful first; empty when the model gave nothing usable.</returns>
    Task<IReadOnlyList<int>> RankAsync(string question, IReadOnlyList<string> passages, CancellationToken ct);
}

/// <summary>The one rerank prompt and output parser, shared by every chat provider.</summary>
public static partial class RerankPrompt
{
    /// <summary>A comma-separated list of up to ~20 numbers.</summary>
    public const int MaxTokens = 60;

    /// <summary>Each passage is cut to this many characters: enough to judge it, small enough for CPU prompt processing.</summary>
    private const int MaxPassageChars = 700;

    public static string Build(string question, IReadOnlyList<string> passages)
    {
        var numbered = string.Join("\n\n", passages.Select((p, i) =>
        {
            var text = p.Trim();
            if (text.Length > MaxPassageChars) text = text[..MaxPassageChars] + "…";
            return $"[{i + 1}] {text}";
        }));

        return $"""
        Which of the numbered passages help answer the question? Reply with their numbers only, most useful first,
        separated by commas (for example: 3, 1). Leave out passages that do not help. If none help, reply 0.

        Question: {question}

        Passages:
        {numbered}

        Numbers:
        """;
    }

    /// <summary>The distinct in-range passage numbers in the model's order, as 0-based indexes.</summary>
    public static IReadOnlyList<int> Parse(string? output, int count)
    {
        var text = AnswerPrompt.StripThinking(output ?? "");
        var result = new List<int>();
        foreach (Match m in Number().Matches(text))
        {
            if (!int.TryParse(m.Value, out var n) || n < 1 || n > count) continue;
            if (!result.Contains(n - 1)) result.Add(n - 1);
        }
        return result;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();
}
