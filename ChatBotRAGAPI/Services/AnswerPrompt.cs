using ChatBotRAGAPI.Contracts;
using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// The one answering prompt, shared by every chat provider (Ollama, OpenAI-compatible, Anthropic) so they all get
/// the same grounding rules, citation format and "I don't have enough data." refusal.
/// </summary>
public static partial class AnswerPrompt
{
    public static string Build(string question, IReadOnlyList<SearchHit> context, IReadOnlyList<ChatMessage> history)
    {
        var passages = string.Join("\n\n", context.Select((h, i) => $"[{i + 1}] (source: {h.Source})\n{h.Text.Trim()}"));

        // Earlier answers cite passages that are not in this prompt, so their [n] markers would point at the wrong text.
        var conversation = history.Count == 0
            ? string.Empty
            : "Conversation so far (use it only to understand what the question refers to, not as a source of facts):\n" +
              string.Join("\n", history.Select(m =>
                  $"{(m.Role == "assistant" ? "Assistant" : "User")}: {CitationMarker().Replace(m.Content, string.Empty).Trim()}")) +
              "\n\n";

        return $"""
        Answer the question using ONLY the numbered context passages below.
        - If the passages do not contain the answer, reply exactly: I don't have enough data.
        - Be concise. After each fact, cite the passage numbers you used in square brackets, e.g. [1] or [1][2].

        Context:
        {passages}

        {conversation}Question: {question}
        Answer:
        """;
    }

    /// <summary>Removes inline reasoning that some models emit instead of using a separate thinking channel.</summary>
    public static string StripThinking(string text) => ThinkBlock().Replace(text, string.Empty).Trim();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"\s*\[\d+\]")]
    private static partial Regex CitationMarker();
}
