using ChatBotRAGAPI.Contracts;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Turns a follow-up ("and what does it cost?") into a question that stands on its own ("What does the Gold SLA
/// cost?") so search finds the right passages. Used for retrieval only: the answer prompt still gets the user's
/// own words plus the conversation.
/// </summary>
public interface IQueryRewriter
{
    /// <returns>The standalone question, or null when the model gave nothing usable.</returns>
    Task<string?> RewriteAsync(string question, IReadOnlyList<ChatMessage> history, CancellationToken ct);
}

/// <summary>The one rewrite prompt and output cleanup, shared by every chat provider.</summary>
public static partial class QueryRewritePrompt
{
    /// <summary>Generous for one question, small enough that a model rambling on can't cost much.</summary>
    public const int MaxTokens = 100;

    /// <summary>Longer earlier answers are cut: the topic is nearly always named near the start.</summary>
    private const int MaxTurnChars = 600;

    private const int MaxQuestionChars = 500;

    public static string Build(string question, IReadOnlyList<ChatMessage> history)
    {
        var conversation = string.Join("\n", history.Select(m =>
        {
            var text = AnswerPrompt.StripCitations(m.Content).Trim();
            if (text.Length > MaxTurnChars) text = text[..MaxTurnChars] + "…";
            return $"{(m.Role == "assistant" ? "Assistant" : "User")}: {text}";
        }));

        return $"""
        Rewrite the user's last question so it can be understood without the conversation: replace pronouns and
        references ("it", "they", "that plan", "the second one") with what they refer to. Keep the user's language
        and meaning; do not answer it and do not add anything. If it already stands on its own, return it unchanged.
        Reply with the question only, on one line.

        Conversation:
        {conversation}

        Last question: {question}
        Standalone question:
        """;
    }

    /// <summary>The first non-empty line, without reasoning, labels or quotes; null if empty or implausibly long.</summary>
    public static string? Clean(string? output)
    {
        var text = AnswerPrompt.StripThinking(output ?? "");
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line is null) return null;

        line = Label().Replace(line, "").Trim().Trim('"', '“', '”', '\'', '`').Trim();
        return line.Length is 0 or > MaxQuestionChars ? null : line;
    }

    [GeneratedRegex(@"^(standalone question|rewritten question|question)\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex Label();
}
