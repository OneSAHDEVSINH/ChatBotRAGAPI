using System.Text.RegularExpressions;

namespace ChatBotRAGAPI.Services;

/// <summary>
/// Tokenizer shared by the no-LLM fallback (<see cref="LocalEmbeddingClient"/>, <see cref="LocalAnswerGenerator"/>).
/// Stopwords are dropped: with bag-of-words scoring, a question and a passage that share only "the"/"is"/"what"
/// would otherwise look related, and those words also dilute the score of genuinely matching passages.
/// </summary>
public static partial class LocalText
{
    private static readonly HashSet<string> Stopwords =
    [
        "a", "about", "after", "all", "also", "am", "an", "and", "any", "are", "as", "at", "be", "been", "before",
        "being", "between", "both", "but", "by", "can", "could", "did", "do", "does", "doing", "during", "each",
        "for", "from", "had", "has", "have", "having", "he", "her", "here", "hers", "him", "his", "how", "i", "if",
        "in", "into", "is", "it", "its", "just", "me", "more", "most", "my", "no", "nor", "not", "of", "on", "only",
        "or", "other", "our", "ours", "out", "over", "own", "same", "she", "should", "so", "some", "such", "than",
        "that", "the", "their", "theirs", "them", "then", "there", "these", "they", "this", "those", "through", "to",
        "too", "under", "until", "up", "very", "was", "we", "were", "what", "when", "where", "which", "while", "who",
        "whom", "why", "will", "with", "would", "you", "your", "yours",
    ];

    public static IEnumerable<string> Tokenize(string text) =>
        Token().Matches(text.ToLowerInvariant())
            .Select(m => m.Value)
            .Where(t => !Stopwords.Contains(t));

    [GeneratedRegex("[a-z0-9]+")]
    private static partial Regex Token();
}
